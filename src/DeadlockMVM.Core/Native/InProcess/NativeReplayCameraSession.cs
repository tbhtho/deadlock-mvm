using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;

namespace DeadlockMVM.Core.Native.InProcess;

/// <summary>
/// Application lifecycle for the explicitly loaded replay-only camera DLL.
/// VConsole remains responsible for replay and spectator commands.
/// </summary>
public sealed class NativeReplayCameraSession : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan BestEffortCleanupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SelfTestRestorationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SelfTestEmergencyCleanupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AutomaticRecoveryTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PausedManualCameraBootstrapDelay = TimeSpan.FromMilliseconds(125);
    private const string QueuedActionLeaseExpiredMessage =
        "The queued SMVM action expired before native camera access began.";
    private const int SeekTickTolerance = 2;
    private const int ObservationTickTolerance = 8;
    private readonly ReplayController _controller;
    private readonly ICameraService _camera;
    private readonly ILogService _log;
    private readonly string _dllPath;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly AsyncLocal<QueuedActionContext?> _queuedActionContext = new();
    private readonly object _manualIntentGate = new();
    private readonly Task _monitorTask;
    private readonly CampathPlaybackStateMachine _playback = new();
    private NativeReplayCameraClient? _client;
    private InProcessCameraStatus? _status;
    private string _message = "Native backend unavailable until replay playback is confirmed.";
    private bool _campathPlaying;
    private bool _holdingKeyframe;
    private bool _manualCameraDesired;
    private int _ownershipCleanupQueued;
    private long _manualRecoveryClaim;
    private long _manualRecoveryClaimConnectionEpoch;
    private long _nextManualRecoveryClaim;
    private int _cameraTransferDepth;
    private long _explicitExitEpoch;
    private long _connectionEpoch;
    private CampathPath? _activePath;
    private CampathEndBehavior _endBehavior = CampathEndBehavior.StopAndRelease;
    private SmvmRendererBackend _loggedRendererBackend;
    private SmvmRendererError _loggedRendererError;
    private SmvmOverlayFlags _loggedRendererLifecycle;
    private SmvmSelfTestResult _selfTestStatus = SmvmSelfTestResult.Idle(SmvmSelfTestKind.Camera);

    public NativeReplayCameraSession(
        ReplayController controller,
        ICameraService camera,
        ILogService log,
        string dllPath)
    {
        _controller = controller;
        _camera = camera;
        _log = log;
        _dllPath = Path.GetFullPath(dllPath);
        _camera.SelectionChanged += OnSelectionChanged;
        _monitorTask = MonitorAsync(_stop.Token);
    }

    public event EventHandler? StatusChanged;
    public event EventHandler<CampathPlaybackStatus>? CampathStateChanged;
    public event EventHandler<SmvmActionDispatch>? SmvmActionReceived;
    public event EventHandler<SmvmSelfTestResult>? SelfTestStateChanged;

    /// <summary>
    /// Supplies the immutable editor snapshot consumed by the in-process UI.
    /// It is called by the native monitor and manual-camera lifecycle operations,
    /// and must not perform UI, pipe, VConsole, or file work.
    /// </summary>
    public Func<SmvmSnapshot>? SmvmSnapshotProvider { get; set; }

    public InProcessBackendState State => _status?.State ?? InProcessBackendState.Unavailable;
    public InProcessCameraStatus? Status => _status;
    public string Message => _message;
    public bool Connected => _client?.Connected == true;
    public long ConnectionEpoch => Volatile.Read(ref _connectionEpoch);
    public bool IsConnectionLeaseCurrent(long expectedEpoch)
    {
        var client = Volatile.Read(ref _client);
        var observedEpoch = ConnectionEpoch;
        return IsConnectionLeaseCurrent(
                   expectedEpoch,
                   observedEpoch,
                   client?.Connected == true) &&
               ReferenceEquals(client, Volatile.Read(ref _client)) &&
               observedEpoch == ConnectionEpoch;
    }
    public bool Available => Connected && _status?.Flags.HasFlag(InProcessStatusFlags.Resolved) == true;
    public bool CampathPlaying => _campathPlaying;
    public bool ManualCameraDesired => Volatile.Read(ref _manualCameraDesired);
    public bool ManualCameraActive => ManualCameraDesired && _status?.ManualCameraActive == true;
    public bool ManualCameraEstablished => IsManualCameraEstablished(ManualCameraDesired, _status);
    public bool CameraOwned => HasCameraOwnershipIntent(
        _campathPlaying,
        _holdingKeyframe,
        ManualCameraDesired,
        _status?.Flags ?? InProcessStatusFlags.None);
    public bool CampathCameraOwned => HasFullCameraOwnershipIntent(
        _campathPlaying,
        _holdingKeyframe,
        _status?.Flags ?? InProcessStatusFlags.None);
    public CampathPlaybackStatus CampathStatus => _playback.Status;
    public SmvmSelfTestResult SelfTestStatus => _selfTestStatus;

    /// <summary>
    /// Carries the immutable overlay action lease through asynchronous native
    /// camera work. Public operations revalidate it only after acquiring the
    /// serialized operation gate, so work queued on replay A cannot recapture
    /// and mutate replacement replay B.
    /// </summary>
    public IDisposable BeginQueuedActionLease(
        Func<bool> stillCurrent,
        ReplayCommandLease? replayLease)
    {
        ArgumentNullException.ThrowIfNull(stillCurrent);
        var previous = _queuedActionContext.Value;
        var current = new QueuedActionContext(stillCurrent, replayLease);
        _queuedActionContext.Value = current;
        return new QueuedActionScope(this, previous, current);
    }

    /// <summary>
    /// Performs a real round trip on the exact native connection that issued a
    /// queued presentation action. This fences independent VConsole profile
    /// writes against native host-loss restoration.
    /// </summary>
    public async Task<bool> ConfirmConnectionEpochAsync(
        long expectedEpoch,
        CancellationToken cancellationToken = default)
    {
        var client = Volatile.Read(ref _client);
        if (!IsConnectionLeaseCurrent(
                expectedEpoch,
                ConnectionEpoch,
                client?.Connected == true))
            return false;

        try
        {
            var status = await client!.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(client, Volatile.Read(ref _client)) ||
                !IsConnectionLeaseCurrent(expectedEpoch, ConnectionEpoch, client.Connected))
                return false;
            UpdateStatus(status);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
                                   InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    internal static bool IsConnectionLeaseCurrent(
        long expectedEpoch,
        long currentEpoch,
        bool connected) =>
        connected && expectedEpoch > 0 && expectedEpoch == currentEpoch;

    /// <summary>
    /// Atomically cancels durable Free Camera intent before serialized cleanup.
    /// Escape/F9 call this at action ingress so a seek or self-test cannot
    /// reacquire ownership while the explicit exit waits for the operation gate.
    /// </summary>
    public void CancelManualCameraIntent()
    {
        var changed = false;
        lock (_manualIntentGate)
        {
            Interlocked.Increment(ref _explicitExitEpoch);
            changed = _manualCameraDesired;
            Volatile.Write(ref _manualCameraDesired, false);
            BlockAutomaticRecoveryLocked();
        }
        if (changed)
            OnStatusChanged();
    }

    /// <summary>
    /// Ensures that SMVM owns the replay camera. Deadlock Free Roam is an internal
    /// prerequisite of this transaction, not a second user-facing camera mode.
    /// Repeated calls are idempotent once a validated game-thread frame is active.
    /// </summary>
    public async Task<InProcessCameraStatus> EnableManualCameraAsync(
        CancellationToken cancellationToken = default)
    {
        var ownershipEpoch = CaptureOwnershipEpoch();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        EnterCameraTransfer();
        try
        {
            ThrowIfQueuedActionLeaseExpired();
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            var client = _client;
            if (client?.Connected != true)
                throw new InvalidOperationException(_message);
            if (!IsConfirmedReplay(_controller.State) || _controller.GameTickOffset is null)
            {
                SetManualCameraDesired(false);
                throw new InvalidOperationException("Replay playback and its tick calibration must be confirmed.");
            }
            if (CampathCameraOwned)
                throw new InvalidOperationException("Stop the current Campath or held keyframe before entering SMVM Free Camera.");

            if (IsManualCameraEstablished(ManualCameraDesired, _status))
            {
                _message = "SMVM Free Camera is already active.";
                OnStatusChanged();
                return _status!;
            }

            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            if (!TrySetManualCameraDesiredForEpoch(ownershipEpoch))
                throw new InvalidOperationException(
                    "SMVM Free Camera activation was superseded by an explicit exit.");
            try
            {
                return await AcquireManualCameraWithObserverRetryAsync(
                    client,
                    cancellationToken,
                    ownershipEpoch).ConfigureAwait(false);
            }
            catch
            {
                using var cleanupTimeout = new CancellationTokenSource(BestEffortCleanupTimeout);
                try
                {
                    await TryDisableManualCameraAsync(client, cleanupTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
                {
                    _log.Warn("Native camera: failed-enable cleanup timed out; the pipe was retired.");
                }
                throw;
            }
        }
        finally
        {
            ExitCameraTransfer();
            _operationGate.Release();
        }
    }

    private async Task<InProcessCameraStatus> AcquireManualCameraCoreAsync(
        NativeReplayCameraClient client,
        CancellationToken cancellationToken,
        bool forceInternalFreeRoam,
        long ownershipEpoch)
    {
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        // A normal F2 activation must not reissue spec_goto while its key/click
        // is still physically down. Force the internal prerequisite only when
        // managed/native evidence says the tracked FreeRoam state is stale, or
        // after a backend reconnect where no native ownership survived.
        if (ShouldEnterInternalFreeRoam(
                _camera.Selection.Mode,
                _status?.Error ?? InProcessErrorCode.None,
                forceInternalFreeRoam))
        {
            ThrowIfQueuedActionLeaseExpired();
            await EnterFreeRoamForCurrentOperationAsync(cancellationToken).ConfigureAwait(false);
        }
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        if (!ManualCameraDesired)
            throw new InvalidOperationException("SMVM Free Camera activation was superseded by an explicit exit.");

        var gateStatus = await SendHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
        if (!IsManualCameraGateReady(gateStatus))
            throw new InvalidOperationException(
                $"Native replay/Free Roam gate is unavailable ({DescribeNativeStatus(gateStatus)}).");
        await PublishFreshSmvmSnapshotAsync(client, required: true, cancellationToken)
            .ConfigureAwait(false);

        var status = await client.PrepareCameraObservationAsync(cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        if (!status.Ready || !IsManualCameraGateReady(status))
            throw new InvalidOperationException(
                $"SMVM Free Camera observation is unavailable: {status.Error}.");
        if (!ManualCameraDesired)
            throw new InvalidOperationException("SMVM Free Camera activation was superseded by an explicit exit.");

        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        var beforeHookCalls = status.HookCalls;
        status = await client.EnableManualCameraAsync(cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        if (!status.ManualCameraRequested)
            throw new InvalidOperationException(
                $"The native backend rejected SMVM Free Camera ownership ({DescribeNativeStatus(status)}).");

        status = await WaitForManualCameraAsync(
            client,
            beforeHookCalls,
            status,
            cancellationToken).ConfigureAwait(false);
        if (!RunQueuedEffectForCurrentOperation(
                () => CompleteManualCameraAcquisitionForEpoch(ownershipEpoch, status)))
            throw new InvalidOperationException(
                "SMVM Free Camera activation was superseded by an explicit exit.");
        _message = "SMVM Free Camera active on a validated game-thread frame.";
        _log.Info($"Native camera: {_message}");
        OnStatusChanged();
        return status;
    }

    private async Task<InProcessCameraStatus> AcquireManualCameraWithObserverRetryAsync(
        NativeReplayCameraClient client,
        CancellationToken cancellationToken,
        long ownershipEpoch)
    {
        if (ShouldBootstrapPausedManualCamera(_controller.State.IsPaused == true, _status))
            await BootstrapPausedManualCameraAsync(cancellationToken, ownershipEpoch).ConfigureAwait(false);
        try
        {
            return await AcquireManualCameraCoreAsync(
                client,
                cancellationToken,
                forceInternalFreeRoam: false,
                ownershipEpoch: ownershipEpoch).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (CanForceRetryManualCameraAcquisition(
                   ManualCameraDesired,
                   IsOwnershipOperationCurrent(ownershipEpoch, CaptureOwnershipEpoch()),
                   _status?.Error ?? InProcessErrorCode.None))
        {
            // The first in-place arm proved that managed FreeRoam tracking was
            // stale. Its two-second proof window also ensures the activation
            // key/click has been released before spec_goto is deliberately retried.
            await TryDisableManualCameraAsync(client, cancellationToken).ConfigureAwait(false);
            if (ShouldBootstrapPausedManualCamera(_controller.State.IsPaused == true, _status))
                await BootstrapPausedManualCameraAsync(cancellationToken, ownershipEpoch).ConfigureAwait(false);
            return await AcquireManualCameraCoreAsync(
                client,
                cancellationToken,
                forceInternalFreeRoam: true,
                ownershipEpoch: ownershipEpoch).ConfigureAwait(false);
        }
    }

    internal static bool CanForceRetryManualCameraAcquisition(
        bool manualCameraDesired,
        bool ownershipEpochCurrent,
        InProcessErrorCode nativeError) =>
        manualCameraDesired && ownershipEpochCurrent &&
        nativeError is InProcessErrorCode.ObserverNotRoaming or
            InProcessErrorCode.ReplayGateClosed or InProcessErrorCode.CameraUnavailable;

    internal static bool ShouldBootstrapPausedManualCamera(
        bool replayPaused,
        InProcessCameraStatus? status) =>
        replayPaused && status?.Flags.HasFlag(InProcessStatusFlags.HookInstalled) != true;

    private async Task BootstrapPausedManualCameraAsync(
        CancellationToken cancellationToken,
        long ownershipEpoch)
    {
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        // A demo launched with +demo_pause can latch before Deadlock creates its
        // roaming observer camera. Keep the transport paused: enter the hidden
        // prerequisite, advance exactly one simulation tick, and give the game
        // window a render turn so SMVM can install its camera hook. Once the
        // hook exists, manual movement and capture continue on render frames
        // without advancing the replay clock.
        ThrowIfQueuedActionLeaseExpired();
        await EnterFreeRoamForCurrentOperationAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfQueuedActionLeaseExpired();
        if (!StepReplayForCurrentOperation())
            throw new InvalidOperationException(
                "Replay changed before the paused Free Camera bootstrap could step it.");
        await Task.Delay(PausedManualCameraBootstrapDelay, cancellationToken).ConfigureAwait(false);
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        _log.Info("Native camera: bootstrapped the paused replay camera with one paused tick step.");
    }

    internal static bool ShouldEnterInternalFreeRoam(
        SpecCameraMode trackedMode,
        InProcessErrorCode nativeError,
        bool forceInternalFreeRoam) =>
        forceInternalFreeRoam || trackedMode != SpecCameraMode.FreeRoam ||
        nativeError == InProcessErrorCode.ObserverNotRoaming;

    internal static bool IsOwnershipOperationCurrent(long capturedEpoch, long currentEpoch) =>
        capturedEpoch == currentEpoch;

    private long CaptureOwnershipEpoch() => Interlocked.Read(ref _explicitExitEpoch);

    private void ThrowIfOwnershipOperationSuperseded(long capturedEpoch)
    {
        if (!IsOwnershipOperationCurrent(capturedEpoch, CaptureOwnershipEpoch()))
            throw new InvalidOperationException(
                "Camera operation was superseded by an explicit SMVM Free Camera exit.");
    }

    /// <summary>
    /// Relinquishes native manual-camera ownership. Local desire is cleared
    /// before waiting for the operation gate so managed state fails closed.
    /// </summary>
    public async Task DisableManualCameraAsync(CancellationToken cancellationToken = default)
    {
        if (_queuedActionContext.Value is null)
            CancelManualCameraIntent();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfQueuedActionLeaseExpired();
            var client = _client;
            if (client?.Connected == true)
            {
                var status = await client.DisableManualCameraAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus(status);
                if (status.ManualCameraRequested || status.ManualCameraActive)
                    throw new InvalidOperationException(
                        $"The native backend did not release SMVM Free Camera ownership ({DescribeNativeStatus(status)}).");
            }

            _message = HasFullCameraOwnershipIntent(
                _campathPlaying,
                _holdingKeyframe,
                _status?.Flags ?? InProcessStatusFlags.None)
                ? "SMVM Free Camera disabled; Campath retains camera ownership."
                : "SMVM Free Camera disabled; Deadlock owns the Free Roam camera.";
            OnStatusChanged();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Explicitly exits the single SMVM Free Camera experience. Unlike Stop,
    /// this always clears durable manual intent and releases manual, path, and
    /// one-shot override ownership before returning control to Deadlock.
    /// </summary>
    public async Task ExitFreeCameraAsync(CancellationToken cancellationToken = default)
    {
        if (_queuedActionContext.Value is null)
            CancelManualCameraIntent();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        EnterCameraTransfer();
        try
        {
            ThrowIfQueuedActionLeaseExpired();
            var hadCampathOwnership = CampathCameraOwned ||
                                       _status?.Flags.HasFlag(InProcessStatusFlags.CampathActive) == true;
            _campathPlaying = false;
            _holdingKeyframe = false;
            _activePath = null;

            if (hadCampathOwnership && _playback.Status.State != CampathPlaybackState.Stopping)
                Transition(CampathPlaybackState.Stopping, "Exiting SMVM Free Camera and releasing the active path.");

            Exception? releaseFailure = null;
            var client = _client;
            if (client?.Connected == true)
            {
                try
                {
                    UpdateStatus(await client.DisableManualCameraAsync(cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    releaseFailure ??= ex;
                    _log.Warn($"Native camera: explicit exit could not confirm manual-camera release: {ex.Message}");
                }

                try
                {
                    UpdateStatus(await client.DisableOverrideAsync(cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    releaseFailure ??= ex;
                    _log.Warn($"Native camera: explicit exit could not confirm override release: {ex.Message}");
                }

                try
                {
                    UpdateStatus(await client.ClearCampathAsync(cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    releaseFailure ??= ex;
                    _log.Warn($"Native camera: explicit exit could not confirm path release: {ex.Message}");
                }
            }

            var remainingFlags = _status?.Flags ?? InProcessStatusFlags.None;
            if (releaseFailure is not null || HasNativeCameraResourcesToRelease(remainingFlags))
            {
                _message = "SMVM Free Camera exit is fail-closed locally, but native camera release was not fully confirmed.";
                if (hadCampathOwnership)
                {
                    _playback.Fail(CampathStartFailure.UnexpectedFailure, _message);
                    RaiseCampathState();
                }
                OnStatusChanged();
                throw new InvalidOperationException(_message, releaseFailure);
            }

            _message = "SMVM Free Camera exited; Deadlock owns the replay camera.";
            if (hadCampathOwnership)
                Transition(CampathPlaybackState.Stopped, _message);
            else
                OnStatusChanged();
        }
        finally
        {
            ExitCameraTransfer();
            _operationGate.Release();
        }
    }

    public Task<SmvmSelfTestResult> RunCameraSelfTestAsync(CancellationToken cancellationToken = default) =>
        RunSelfTestAsync(SmvmSelfTestKind.Camera, null, cancellationToken);

    public Task<SmvmSelfTestResult> RunCampathSelfTestAsync(
        CampathPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return RunSelfTestAsync(SmvmSelfTestKind.Campath, path, cancellationToken);
    }

    private async Task<SmvmSelfTestResult> RunSelfTestAsync(
        SmvmSelfTestKind kind,
        CampathPath? path,
        CancellationToken cancellationToken)
    {
        var ownershipEpoch = CaptureOwnershipEpoch();
        var result = SmvmSelfTestResult.Idle(kind);
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = CompleteSelfTestFailure(
                kind, SmvmSelfTestFailure.Cancelled, "Self-test cancelled before camera access began.",
                stage: SmvmSelfTestStage.Cancelled);
            PublishSelfTest(result);
            return result;
        }

        EnterCameraTransfer();
        SmvmSelfTestRestorationPolicy? restoration = null;
        NativeReplayCameraClient? client = null;
        try
        {
            ThrowIfQueuedActionLeaseExpired();
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            PublishSelfTest(kind, SmvmSelfTestStage.Validating, "Validating replay and native camera gates.");
            client = _client;
            var replay = _controller.State;
            var currentTick = replay.CurrentTick ?? -1;
            var initialFailure = SmvmSelfTestPolicy.ValidateCameraPrerequisites(
                client?.Connected == true,
                IsConfirmedReplay(replay) && _controller.GameTickOffset is not null,
                _camera.Selection.Mode == SpecCameraMode.FreeRoam,
                cameraObserved: true,
                CampathCameraOwned);
            if (initialFailure != SmvmSelfTestFailure.None)
            {
                result = CompleteSelfTestFailure(kind, initialFailure, SelfTestFailureDetail(initialFailure));
                return result;
            }
            var connectedClient = client!;
            if (kind == SmvmSelfTestKind.Campath &&
                SmvmSelfTestPolicy.ValidateCampath(path, currentTick) is { } pathFailure &&
                pathFailure != SmvmSelfTestFailure.None)
            {
                result = CompleteSelfTestFailure(kind, pathFailure, SelfTestFailureDetail(pathFailure));
                return result;
            }

            var heartbeat = await SendHeartbeatAsync(connectedClient, cancellationToken).ConfigureAwait(false);
            if (!IsManualCameraGateReady(heartbeat))
            {
                result = CompleteSelfTestFailure(
                    kind,
                    SmvmSelfTestFailure.ReplayUnavailable,
                    $"Native replay gate rejected the self-test ({DescribeNativeStatus(heartbeat)})." );
                return result;
            }

            PublishSelfTest(kind, SmvmSelfTestStage.CapturingBaseline,
                "Capturing a fresh authoritative game-thread camera frame.");
            var baselineStatus = await CaptureFreshSelfTestFrameAsync(connectedClient, heartbeat, cancellationToken)
                .ConfigureAwait(false);
            var prerequisite = SmvmSelfTestPolicy.ValidateCameraPrerequisites(
                connected: true,
                replayGate: IsManualCameraGateReady(baselineStatus),
                freeRoam: _camera.Selection.Mode == SpecCameraMode.FreeRoam,
                cameraObserved: baselineStatus.CameraObserved && baselineStatus.Camera.IsValid,
                fullCameraOwned: CampathCameraOwned);
            if (prerequisite != SmvmSelfTestFailure.None)
            {
                result = CompleteSelfTestFailure(kind, prerequisite, SelfTestFailureDetail(prerequisite));
                return result;
            }

            restoration = SmvmSelfTestPolicy.CreateRestorationPolicy(
                baselineStatus.Camera,
                ManualCameraDesired,
                baselineStatus.RollOverrideActive);
            PublishSelfTest(kind, SmvmSelfTestStage.ReleasingPriorOwnership,
                "Temporarily releasing SMVM Free Camera controls for an isolated probe.");
            SetManualCameraDesired(false);
            if (baselineStatus.ManualCameraRequested || baselineStatus.ManualCameraActive ||
                baselineStatus.RollOverrideActive)
                UpdateStatus(await connectedClient.DisableManualCameraAsync(cancellationToken).ConfigureAwait(false));

            if (kind == SmvmSelfTestKind.Camera)
            {
                var probe = SmvmSelfTestPolicy.CreateCameraProbe(restoration.Value.Baseline);
                PublishSelfTest(kind, SmvmSelfTestStage.ApplyingProbe,
                    "Applying a bounded FOV and roll probe on the game thread.", probe);
                var acceptedBefore = baselineStatus.AcceptedSequence;
                ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                var transfer = await connectedClient.SetCameraSampleAsync(probe, cancellationToken).ConfigureAwait(false);
                UpdateStatus(transfer);
                ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                if (!transfer.Flags.HasFlag(InProcessStatusFlags.HasSample) ||
                    transfer.AcceptedSequence <= acceptedBefore)
                {
                    result = CompleteSelfTestFailure(kind, SmvmSelfTestFailure.ProbeTransferRejected,
                        $"Native backend rejected the typed camera probe ({DescribeNativeStatus(transfer)}).",
                        probe, transfer.Camera);
                }
                else
                {
                    var beforeHookCalls = transfer.HookCalls;
                    ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                    var armed = await connectedClient.EnableOverrideAsync(cancellationToken).ConfigureAwait(false);
                    UpdateStatus(armed);
                    ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                    PublishSelfTest(kind, SmvmSelfTestStage.VerifyingAuthoritativeFrame,
                        "Verifying the rendered camera probe through authoritative readback.", probe);
                    var verified = await PollSelfTestStatusAsync(
                        connectedClient,
                        status => status.OverrideActive && status.CameraObserved &&
                                  status.HookCalls > beforeHookCalls &&
                                  SmvmSelfTestPolicy.SamplesMatch(probe, status.Camera),
                        armed,
                        cancellationToken).ConfigureAwait(false);
                    ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                    if (!verified.OverrideActive)
                        result = CompleteSelfTestFailure(kind, SmvmSelfTestFailure.NativeOwnershipRejected,
                            $"Native camera ownership was not acquired ({DescribeNativeStatus(verified)}).", probe, verified.Camera);
                    else if (!verified.CameraObserved || !SmvmSelfTestPolicy.SamplesMatch(probe, verified.Camera))
                        result = CompleteSelfTestFailure(kind, SmvmSelfTestFailure.AuthoritativeMismatch,
                            "The authoritative frame did not match the bounded FOV and roll probe.", probe, verified.Camera);
                    else
                        result = CompleteSelfTestSuccess(kind,
                            "Camera self-test passed exact game-thread FOV and roll readback.", probe, verified.Camera);
                }
            }
            else
            {
                var pathDescription = $"{path!.Keyframes.Count} keys, first tick {path.Keyframes[0].DemoTick}, " +
                                      $"last tick {path.Keyframes[^1].DemoTick}";
                PublishSelfTest(kind, SmvmSelfTestStage.TransferringPath,
                    $"Transferring editor path ({pathDescription}) without seeking.");
                var acceptedBefore = baselineStatus.AcceptedSequence;
                ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                var transfer = await connectedClient.SetCampathAsync(
                        path, CampathEndBehavior.HoldFinalCamera, cancellationToken)
                    .ConfigureAwait(false);
                UpdateStatus(transfer);
                ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                if (!transfer.Flags.HasFlag(InProcessStatusFlags.CampathActive) ||
                    !transfer.Flags.HasFlag(InProcessStatusFlags.HasSample) ||
                    transfer.AcceptedSequence <= acceptedBefore)
                {
                    result = CompleteSelfTestFailure(kind, SmvmSelfTestFailure.PathTransferRejected,
                        $"Native backend rejected editor path ({pathDescription}; {DescribeNativeStatus(transfer)}).",
                        actual: transfer.Camera);
                }
                else
                {
                    var beforeHookCalls = transfer.HookCalls;
                    PublishSelfTest(kind, SmvmSelfTestStage.AcquiringOwnership,
                        "Acquiring native Campath ownership at the current replay tick.");
                    ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                    var armed = await connectedClient.EnableOverrideAsync(cancellationToken).ConfigureAwait(false);
                    UpdateStatus(armed);
                    ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                    PublishSelfTest(kind, SmvmSelfTestStage.VerifyingAuthoritativeFrame,
                        "Evaluating the current replay tick against authoritative camera readback.");
                    var verified = await PollSelfTestStatusAsync(
                        connectedClient,
                        status => status.ReplayTick >= 0 && status.OverrideActive &&
                                  status.Flags.HasFlag(InProcessStatusFlags.CampathActive) &&
                                  status.CameraObserved && status.HookCalls > beforeHookCalls &&
                                  SmvmSelfTestPolicy.SamplesMatch(path.Evaluate(status.ReplayTick), status.Camera),
                        armed,
                        cancellationToken).ConfigureAwait(false);
                    ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                    var expected = path.Evaluate(verified.ReplayTick >= 0 ? verified.ReplayTick : currentTick);
                    if (!verified.OverrideActive || !verified.Flags.HasFlag(InProcessStatusFlags.CampathActive))
                        result = CompleteSelfTestFailure(kind, SmvmSelfTestFailure.NativeOwnershipRejected,
                            $"Native Campath ownership was not acquired ({pathDescription}; " +
                            $"{DescribeNativeStatus(verified)}).", expected, verified.Camera);
                    else if (!verified.CameraObserved || !SmvmSelfTestPolicy.SamplesMatch(expected, verified.Camera))
                        result = CompleteSelfTestFailure(kind, SmvmSelfTestFailure.AuthoritativeMismatch,
                            $"Campath sample at replay tick {verified.ReplayTick} did not match readback " +
                            $"({pathDescription}).", expected, verified.Camera);
                    else
                        result = CompleteSelfTestSuccess(kind,
                            $"Campath self-test passed at replay tick {verified.ReplayTick} without seeking " +
                            $"({pathDescription}).", expected, verified.Camera);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = CompleteSelfTestFailure(kind, SmvmSelfTestFailure.Cancelled, "Self-test cancelled.",
                stage: SmvmSelfTestStage.Cancelled);
        }
        catch (Exception ex)
        {
            result = CompleteSelfTestFailure(kind, SmvmSelfTestFailure.UnexpectedFailure,
                $"Self-test failed safely: {ex.Message}");
        }
        finally
        {
            if (restoration is { } policy)
            {
                if (!IsOwnershipOperationCurrent(ownershipEpoch, CaptureOwnershipEpoch()))
                {
                    SetManualCameraDesired(false);
                    if (client?.Connected == true)
                        await BestEffortReleaseFullOverrideAsync(
                            client,
                            "superseded self-test cleanup").ConfigureAwait(false);
                    result = CompleteSelfTestFailure(
                        kind,
                        SmvmSelfTestFailure.Cancelled,
                        "Self-test restoration was superseded by an explicit SMVM Free Camera exit.",
                        stage: SmvmSelfTestStage.Cancelled);
                }
                else if (client?.Connected != true)
                {
                    if (!policy.RestoreManualCamera ||
                        !TrySetManualCameraDesiredForEpoch(ownershipEpoch))
                        SetManualCameraDesired(false);
                    result = CompleteSelfTestFailure(
                        kind,
                        SmvmSelfTestFailure.RestorationFailed,
                        policy.RestoreManualCamera
                            ? "Self-test restoration failed closed because the native backend disconnected; " +
                              "Free Camera intent was preserved for explicit reacquisition."
                            : "Self-test restoration failed closed because the native backend disconnected.");
                }
                else
                {
                    var restorationError = await RestoreAfterSelfTestAsync(
                        client,
                        kind,
                        policy,
                        ownershipEpoch).ConfigureAwait(false);
                    if (restorationError is not null)
                        result = CompleteSelfTestFailure(kind, SmvmSelfTestFailure.RestorationFailed, restorationError);
                }
            }
            PublishSelfTest(result);
            ExitCameraTransfer();
            _operationGate.Release();
        }

        return result;
    }

    /// <summary>
    /// Captures one renderer-facing camera frame without pausing, seeking, changing POV,
    /// or acquiring override ownership.
    /// </summary>
    public async Task<CampathKeyframe> CaptureCurrentCameraAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfQueuedActionLeaseExpired();
            var client = _client ?? throw new InvalidOperationException(_message);
            if (_campathPlaying)
                throw new InvalidOperationException("Stop Campath playback before adding a keyframe.");
            if (!IsConfirmedReplay(_controller.State) || _controller.GameTickOffset is null)
                throw new InvalidOperationException("Replay playback and its tick calibration must be confirmed.");
            if (!IsManualCameraEstablished(ManualCameraDesired, _status))
                throw new InvalidOperationException(
                    "Enter SMVM Free Camera before adding or updating a keyframe.");

            var before = await SendHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
            var status = await client.PrepareCameraObservationAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            if (!status.Ready)
                throw new InvalidOperationException($"Native camera observation is unavailable: {status.Error}.");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while ((!IsManualCameraEstablished(ManualCameraDesired, status) ||
                    status.HookCalls <= before.HookCalls) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus(status);
            }
            if (!IsManualCameraEstablished(ManualCameraDesired, status) ||
                status.HookCalls <= before.HookCalls)
                throw new InvalidOperationException(
                    $"A fresh SMVM Free Camera frame was not observed: {status.Error}.");

            return new CampathKeyframe(status.ReplayTick, status.Camera);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Applies a narrowly typed roll-only override to an established SMVM Free
    /// Camera sample on the game-thread update. It cannot acquire camera ownership.
    /// </summary>
    public async Task<InProcessCameraStatus> SetManualRollAsync(
        double roll,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(roll) || roll is < -180.0 or > 180.0)
            throw new ArgumentOutOfRangeException(nameof(roll));

        var ownershipEpoch = CaptureOwnershipEpoch();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfQueuedActionLeaseExpired();
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            var client = _client ?? throw new InvalidOperationException(_message);
            if (_campathPlaying || _status?.OverrideActive == true)
                throw new InvalidOperationException("Release Campath camera ownership before changing manual roll.");
            if (!IsConfirmedReplay(_controller.State) || _controller.GameTickOffset is null)
                throw new InvalidOperationException("Replay playback and its tick calibration must be confirmed.");
            if (!IsManualCameraEstablished(ManualCameraDesired, _status))
                throw new InvalidOperationException("Enter SMVM Free Camera before changing rendered roll.");

            var before = await SendHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
            if (!IsManualCameraEstablished(ManualCameraDesired, before))
                throw new InvalidOperationException("SMVM Free Camera ownership was lost before rendered roll could be changed.");
            var status = await client.PrepareCameraObservationAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            if (!status.Ready || !ManualCameraDesired ||
                !status.ManualCameraRequested || !status.ManualCameraActive)
                throw new InvalidOperationException($"Native roll control is unavailable: {status.Error}.");

            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            status = await client.SetRollOverrideAsync(roll, cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while ((!IsManualCameraEstablished(ManualCameraDesired, status) ||
                    !status.RollOverrideActive || status.HookCalls <= before.HookCalls ||
                    Math.Abs(ShortestAngleDelta(status.Camera.Roll, roll)) > 0.05) &&
                   DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus(status);
            }
            if (!IsManualCameraEstablished(ManualCameraDesired, status) ||
                !status.RollOverrideActive || status.HookCalls <= before.HookCalls ||
                Math.Abs(ShortestAngleDelta(status.Camera.Roll, roll)) > 0.05)
                throw new InvalidOperationException($"Rendered roll did not reach {roll:0.0} degrees: {status.Error}.");

            _log.Info($"Native camera: rendered roll set to {roll:0.0} degrees on a validated game-thread frame.");
            return status;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<InProcessCameraStatus> PlayCampathAsync(
        CampathPath path,
        CampathPlayMode playMode = CampathPlayMode.FromStart,
        CampathEndBehavior endBehavior = CampathEndBehavior.StopAndRelease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        var ownershipEpoch = CaptureOwnershipEpoch();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        EnterCameraTransfer();
        var originalTransportCaptured = false;
        bool? originalTransportWasPaused = null;
        var playbackCommitted = false;
        try
        {
            ThrowIfQueuedActionLeaseExpired();
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            Transition(CampathPlaybackState.Validating, "Validating replay, path, and native backend.");
            if (!path.IsValid)
                ThrowStartFailure(CampathStartFailure.PathHasTooFewKeyframes,
                    "Campath requires 2-128 valid keyframes at unique increasing replay ticks.");
            if (!Enum.IsDefined(playMode) || !Enum.IsDefined(endBehavior) || endBehavior == CampathEndBehavior.Loop)
                ThrowStartFailure(CampathStartFailure.UnexpectedFailure,
                    endBehavior == CampathEndBehavior.Loop
                        ? "Loop is unavailable until deterministic replay seeking can be guaranteed."
                        : "The selected Campath playback option is invalid.");

            var client = _client;
            if (client?.Connected != true)
                ThrowStartFailure(CampathStartFailure.NativeBackendDisconnected, _message);
            if (!IsConfirmedReplay(_controller.State) || _controller.GameTickOffset is null)
                ThrowStartFailure(CampathStartFailure.ReplayUnavailable,
                    "Replay playback and its tick calibration must be confirmed.");
            if (!ManualCameraDesired)
                ThrowStartFailure(CampathStartFailure.NotInFreeRoam,
                    "Enter SMVM Free Camera before playing a Campath.");
            if (!IsManualCameraEstablished(ManualCameraDesired, _status))
            {
                try
                {
                    await AcquireManualCameraWithObserverRetryAsync(
                        client,
                        cancellationToken,
                        ownershipEpoch).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not CampathStartException)
                {
                    ThrowStartFailure(
                        CampathStartFailure.NotInFreeRoam,
                        $"SMVM Free Camera could not be prepared for path playback: {ex.Message}",
                        ex);
                }
            }

            var originalState = _controller.State;
            originalTransportWasPaused = originalState.IsPaused;
            originalTransportCaptured = true;
            var currentTick = originalState.CurrentTick!.Value;
            int? observationExpectedTick = playMode == CampathPlayMode.FromStart
                ? checked((int)path.Keyframes[0].DemoTick)
                : null;
            var startSeekRequired = playMode == CampathPlayMode.FromStart &&
                                    Math.Abs((long)currentTick - path.Keyframes[0].DemoTick) > 2;
            var seamlessManualHandoff = CanUseSeamlessManualToPathHandoff(
                IsManualCameraEstablished(ManualCameraDesired, _status),
                startSeekRequired);
            if (playMode == CampathPlayMode.FromCurrent &&
                (currentTick < path.Keyframes[0].DemoTick || currentTick > path.Keyframes[^1].DemoTick))
                ThrowStartFailure(CampathStartFailure.CurrentTickOutsidePath,
                    $"Current replay tick {currentTick} is outside the Campath range " +
                    $"{path.Keyframes[0].DemoTick}-{path.Keyframes[^1].DemoTick}.");

            // Keep the established manual writer armed while the typed path is
            // transferred. EnableOverride takes precedence on the next hook
            // frame, producing a direct SMVM manual-to-path handoff with no
            // intervening Deadlock-owned observer frame when no seek is needed.
            Transition(CampathPlaybackState.PreparingFreeRoam,
                "Preparing the active SMVM Free Camera for path ownership.");

            if (playMode == CampathPlayMode.FromStart)
            {
                var startTick = checked((int)path.Keyframes[0].DemoTick);
                if (startSeekRequired)
                {
                    Transition(CampathPlaybackState.SeekingToStart,
                        "SKIPPING TO CAMPATH START", startTick, currentTick);
                    if (originalState.IsPaused != true && !PauseReplayForCurrentOperation())
                        throw new InvalidOperationException("Replay changed before Campath start could pause it.");
                    Transition(CampathPlaybackState.WaitingForLandedTick,
                        "Waiting for Deadlock to report the landed replay tick.", startTick, currentTick);
                    int landed;
                    try
                    {
                        landed = await SeekToPathStartAsync(startTick, cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException ex)
                    {
                        ThrowStartFailure(CampathStartFailure.SeekTimedOut,
                            $"Deadlock did not land at Campath start tick {startTick} within 15 seconds.", ex);
                        throw;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not CampathStartException)
                    {
                        ThrowStartFailure(CampathStartFailure.SeekFailed,
                            $"Campath start seek failed: {ex.Message}", ex);
                        throw;
                    }
                    observationExpectedTick = landed;
                    if (!PauseReplayForCurrentOperation())
                        throw new InvalidOperationException("Replay changed while Campath was landing at its start tick.");
                    Transition(CampathPlaybackState.ReacquiringFreeRoam,
                        "Reacquiring Free Roam after the landed seek.", startTick, landed);
                    try
                    {
                        ThrowIfQueuedActionLeaseExpired();
                        await EnterFreeRoamForCurrentOperationAsync(cancellationToken).ConfigureAwait(false);
                        await SynchronizeObserverOriginAfterSeekAsync(
                            path.Keyframes[0].Camera,
                            cancellationToken,
                            ownershipEpoch).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ThrowStartFailure(CampathStartFailure.FreeRoamReacquisitionFailed,
                            $"Free Roam could not be reacquired after seek: {ex.Message}", ex);
                    }
                }
            }

            var gateStatus = await SendHeartbeatAsync(client!, cancellationToken).ConfigureAwait(false);
            gateStatus = await WaitForNativeAsync(
                client!,
                status => (status.State is InProcessBackendState.Connected or InProcessBackendState.Ready) &&
                    status.Flags.HasFlag(InProcessStatusFlags.Resolved) &&
                    status.Flags.HasFlag(InProcessStatusFlags.ReplayGate),
                TimeSpan.FromSeconds(2),
                CampathStartFailure.FreeRoamReacquisitionFailed,
                "Native replay/Free Roam gate did not become ready.",
                gateStatus,
                cancellationToken).ConfigureAwait(false);
            if (seamlessManualHandoff)
            {
                if (!IsManualCameraEstablished(ManualCameraDesired, gateStatus))
                    ThrowStartFailure(
                        CampathStartFailure.CameraOwnershipRejected,
                        "The validated SMVM Free Camera frame was lost before path handoff.");
            }
            else
            {
                gateStatus = await WaitForFreshStrictFreeRoamCameraAsync(
                    client!,
                    gateStatus,
                    observationExpectedTick,
                    "A fresh Free Roam camera frame was not observed before Campath transfer.",
                    cancellationToken).ConfigureAwait(false);
            }

            Transition(CampathPlaybackState.TransferringPath,
                $"Transferring {path.Keyframes.Count} typed keyframes to the native replay camera.");
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            var transferStatus = await client!.SetCampathAsync(path, endBehavior, cancellationToken).ConfigureAwait(false);
            UpdateStatus(transferStatus);
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            if (!transferStatus.Flags.HasFlag(InProcessStatusFlags.CampathActive) ||
                !transferStatus.Flags.HasFlag(InProcessStatusFlags.HasSample))
                ThrowFromNative(transferStatus, CampathStartFailure.PathTransferFailed,
                    "The native backend rejected the Campath payload.");

            Transition(CampathPlaybackState.ArmingNativeCamera,
                "Requesting terminal spectator-camera ownership.");
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            var status = await client.EnableOverrideAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            if (!status.Flags.HasFlag(InProcessStatusFlags.OverrideRequested))
                ThrowFromNative(status, CampathStartFailure.NativePathNotArmed,
                    "The native backend did not arm the transferred path.");

            // EnableOverride is acknowledged on the pipe thread. OverrideActive is
            // deliberately produced only by the next validated game-thread camera
            // hook frame, so this transition must wait for that authoritative signal.
            Transition(CampathPlaybackState.WaitingForOwnership,
                "Waiting for the next validated spectator-camera frame.");
            status = await WaitForNativeAsync(
                client,
                candidate => candidate.OverrideActive &&
                    candidate.Flags.HasFlag(InProcessStatusFlags.CampathActive) &&
                    candidate.AppliedSequence >= transferStatus.AcceptedSequence,
                TimeSpan.FromSeconds(2),
                CampathStartFailure.CameraOwnershipRejected,
                "Native Campath did not acquire the spectator camera.",
                status,
                cancellationToken).ConfigureAwait(false);
            status = await EnsureManualCameraArmedUnderOverrideAsync(
                client,
                status,
                cancellationToken,
                ownershipEpoch).ConfigureAwait(false);

            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            _campathPlaying = true;
            _holdingKeyframe = false;
            _activePath = path;
            _endBehavior = endBehavior;
            if (!PlayReplayForCurrentOperation())
                throw new InvalidOperationException("Replay changed before Campath playback could start.");
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            playbackCommitted = true;
            Transition(CampathPlaybackState.Playing,
                $"{path.Interpolation} Campath active: tick {path.Keyframes[0].DemoTick} to {path.Keyframes[^1].DemoTick}.");
            _message = _playback.Status.Detail;
            _log.Info($"Native camera: {_message}");
            OnStatusChanged();
            return status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _playback.Cancel("Campath start cancelled.");
            RaiseCampathState();
            if (_client is { Connected: true } client)
                await BestEffortReleaseFullOverrideAsync(client, "cancelled Play cleanup")
                    .ConfigureAwait(false);
            if (originalTransportCaptured && !playbackCommitted)
                RestoreReplayTransportState(originalTransportWasPaused);
            throw;
        }
        catch (Exception ex)
        {
            if (ex is not CampathStartException)
            {
                _playback.Fail(CampathStartFailure.UnexpectedFailure, $"Unexpected Campath start failure: {ex.Message}");
                _message = _playback.Status.Detail;
                _log.Warn($"Native camera: Campath start failed [UnexpectedFailure] {_message}");
                RaiseCampathState();
                OnStatusChanged();
            }
            _campathPlaying = false;
            _holdingKeyframe = false;
            _activePath = null;
            if (_client is { Connected: true } client)
                await BestEffortReleaseFullOverrideAsync(client, "failed Play cleanup")
                    .ConfigureAwait(false);
            if (originalTransportCaptured && !playbackCommitted)
                RestoreReplayTransportState(originalTransportWasPaused);
            if (ex is CampathStartException)
                throw;
            throw new CampathStartException(CampathStartFailure.UnexpectedFailure, _message, ex);
        }
        finally
        {
            ExitCameraTransfer();
            _operationGate.Release();
        }
    }

    public Task<InProcessCameraStatus> PlayLinearCampathAsync(
        LinearCampath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return PlayCampathAsync(
            new CampathPath(new[] { path.From, path.To }),
            CampathPlayMode.FromStart,
            CampathEndBehavior.StopAndRelease,
            cancellationToken);
    }

    internal static bool CanUseSeamlessManualToPathHandoff(
        bool manualCameraEstablished,
        bool seekRequired) =>
        manualCameraEstablished && !seekRequired;

    public async Task<InProcessCameraStatus> GoToKeyframeAsync(
        CampathKeyframe keyframe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyframe);
        if (!keyframe.IsValid)
            throw new ArgumentOutOfRangeException(nameof(keyframe));

        var ownershipEpoch = CaptureOwnershipEpoch();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        EnterCameraTransfer();
        try
        {
            ThrowIfQueuedActionLeaseExpired();
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            var client = _client ?? throw new InvalidOperationException(_message);
            if (!IsConfirmedReplay(_controller.State) || _controller.GameTickOffset is null)
                throw new InvalidOperationException("Replay playback and its tick calibration must be confirmed.");
            if (!ManualCameraDesired)
                throw new InvalidOperationException("Enter SMVM Free Camera before going to a keyframe.");

            // A prior Go To intentionally holds a full native override. Release
            // that ownership before seeking so the old shot cannot reappear if
            // Deadlock temporarily leaves and then re-enters Free Roam.
            if (CampathCameraOwned)
                await StopCampathCoreAsync(cancellationToken).ConfigureAwait(false);

            if (!PauseReplayForCurrentOperation())
                throw new InvalidOperationException("Replay changed before Go To could pause it.");
            var landed = await SeekToPathStartAsync(
                checked((int)keyframe.DemoTick),
                cancellationToken).ConfigureAwait(false);
            if (!PauseReplayForCurrentOperation())
                throw new InvalidOperationException("Replay changed before the active Campath seek could pause it.");
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            ThrowIfQueuedActionLeaseExpired();
            await EnterFreeRoamForCurrentOperationAsync(cancellationToken).ConfigureAwait(false);
            await SynchronizeObserverOriginAfterSeekAsync(
                keyframe.Camera,
                cancellationToken,
                ownershipEpoch).ConfigureAwait(false);
            // EnterFreeRoamAsync completes from authoritative spectator state, but
            // the renderer camera can still be between observer instances for a
            // few frames after demo_gototick. Prove that the post-seek instance is
            // observable before arming a one-shot override; otherwise the first
            // hook frame can correctly revoke ownership as ObserverNotRoaming.
            var gateStatus = await SendHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
            await WaitForFreshStrictFreeRoamCameraAsync(
                client,
                gateStatus,
                landed,
                "A fresh post-seek Free Roam camera frame was not observed.",
                cancellationToken).ConfigureAwait(false);
            var status = await RebaseManualCameraAtSampleAsync(
                client,
                keyframe.Camera,
                cancellationToken,
                ownershipEpoch).ConfigureAwait(false);
            _campathPlaying = false;
            _activePath = null;
            _holdingKeyframe = false;
            _message = $"SMVM Free Camera positioned at keyframe tick {keyframe.DemoTick}.";
            OnStatusChanged();
            return status;
        }
        catch
        {
            // Any failure after SetCameraSample/EnableOverride must release the
            // possibly armed request. Otherwise returning to Free Roam can snap
            // back to a half-completed Go To shot.
            _holdingKeyframe = false;
            _campathPlaying = false;
            _activePath = null;
            if (_client is { Connected: true } client)
                await BestEffortReleaseFullOverrideAsync(client, "failed Go To cleanup").ConfigureAwait(false);
            throw;
        }
        finally
        {
            ExitCameraTransfer();
            _operationGate.Release();
        }
    }

    public async Task StopCampathAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfQueuedActionLeaseExpired();
            await StopCampathCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Seeks through the existing replay controller while preserving an active
    /// Campath. Deadlock temporarily leaves observer mode 4 during demo_gototick;
    /// the native hook therefore fails closed until the landed tick is reported,
    /// Free Roam is reacquired, and a new game-thread camera frame owns the view.
    /// </summary>
    public async Task<int?> SeekReplayAsync(int tick, CancellationToken cancellationToken = default)
    {
        if (tick < 0)
            throw new ArgumentOutOfRangeException(nameof(tick));

        var ownershipEpoch = CaptureOwnershipEpoch();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var preservingManualCamera = false;
        var protectedManualSeek = false;
        var activeCampathSeek = false;
        var restoreProtectedTransport = false;
        bool? protectedTransportWasPaused = null;
        try
        {
            ThrowIfQueuedActionLeaseExpired();
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            if (!_campathPlaying || _activePath is not { } path)
            {
                var releasingHeldCamera = CampathCameraOwned;
                if (!ManualCameraDesired && !releasingHeldCamera)
                {
                    return await SeekReplayAndWaitForLandingAsync(tick, cancellationToken)
                        .ConfigureAwait(false);
                }

                EnterCameraTransfer();
                preservingManualCamera = true;
                if (releasingHeldCamera)
                    await StopCampathCoreAsync(cancellationToken).ConfigureAwait(false);
                if (!ManualCameraDesired)
                {
                    return await SeekReplayAndWaitForLandingAsync(tick, cancellationToken)
                        .ConfigureAwait(false);
                }

                protectedManualSeek = true;
                ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                var manualClient = _client;
                if (manualClient?.Connected != true)
                    throw new InvalidOperationException(_message);

                var baselineStatus = await SendHeartbeatAsync(manualClient, cancellationToken).ConfigureAwait(false);
                if (!TryGetManualSeekBaseline(ManualCameraDesired, baselineStatus, out var baseline))
                    throw new InvalidOperationException(
                        "A validated SMVM Free Camera frame is required before seeking. " +
                        "Free Camera intent remains requested; reacquire it and retry.");

                var manualWasPaused = _controller.State.IsPaused;
                protectedTransportWasPaused = manualWasPaused;
                restoreProtectedTransport = true;
                if (!PauseReplayForCurrentOperation())
                    throw new InvalidOperationException("Replay changed before the Free Camera seek could pause it.");
                int manualLanded;
                try
                {
                    manualLanded = await SeekToPathStartAsync(tick, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new InvalidOperationException(
                        $"Deadlock did not land at requested tick {tick} within 15 seconds.", ex);
                }

                if (!PauseReplayForCurrentOperation())
                    throw new InvalidOperationException("Replay changed while the Free Camera seek was landing.");
                if (!ManualCameraDesired)
                    throw new InvalidOperationException(
                        "Replay seek landed after an explicit SMVM Free Camera exit; camera ownership will remain released.");
                ThrowIfQueuedActionLeaseExpired();
                await EnterFreeRoamForCurrentOperationAsync(cancellationToken).ConfigureAwait(false);
                await SynchronizeObserverOriginAfterSeekAsync(
                    baseline,
                    cancellationToken,
                    ownershipEpoch).ConfigureAwait(false);
                var manualGateStatus = await SendHeartbeatAsync(manualClient, cancellationToken).ConfigureAwait(false);
                manualGateStatus = await WaitForNativeAsync(
                    manualClient,
                    status => status.Flags.HasFlag(InProcessStatusFlags.Resolved) &&
                              status.Flags.HasFlag(InProcessStatusFlags.ReplayGate),
                    TimeSpan.FromSeconds(2),
                    CampathStartFailure.FreeRoamReacquisitionFailed,
                    "Native replay/Free Roam gate did not reopen after seek.",
                    manualGateStatus,
                    cancellationToken).ConfigureAwait(false);
                await WaitForFreshStrictFreeRoamCameraAsync(
                    manualClient,
                    manualGateStatus,
                    manualLanded,
                    "A fresh post-seek Free Roam frame was not observed before restoring SMVM Free Camera.",
                    cancellationToken).ConfigureAwait(false);
                await RebaseManualCameraAtSampleAsync(
                    manualClient,
                    baseline,
                    cancellationToken,
                    ownershipEpoch).ConfigureAwait(false);

                _message = $"SMVM Free Camera preserved at landed replay tick {manualLanded}.";
                _log.Info($"Native camera: {_message}");
                OnStatusChanged();
                return manualLanded;
            }

            activeCampathSeek = true;
            EnterCameraTransfer();
            preservingManualCamera = true;

            var client = _client;
            if (client?.Connected != true)
                throw new CampathStartException(CampathStartFailure.NativeBackendDisconnected, _message);

            var wasPaused = _controller.State.IsPaused;
            protectedTransportWasPaused = wasPaused;
            restoreProtectedTransport = true;
            if (!PauseReplayForCurrentOperation())
                throw new InvalidOperationException("Replay changed while the active Campath seek was landing.");
            var beforeTick = _controller.State.CurrentTick;
            Transition(
                CampathPlaybackState.WaitingForLandedTick,
                $"Seeking active Campath to tick {tick}; native writes are fail-closed until landing.",
                tick,
                beforeTick);

            int landed;
            try
            {
                landed = await SeekToPathStartAsync(tick, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new CampathStartException(
                    CampathStartFailure.SeekTimedOut,
                    $"Deadlock did not land at requested tick {tick} within 15 seconds.",
                    ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not CampathStartException)
            {
                throw new CampathStartException(
                    CampathStartFailure.SeekFailed,
                    $"Active Campath seek failed: {ex.Message}",
                    ex);
            }

            Transition(
                CampathPlaybackState.ReacquiringFreeRoam,
                $"Landed at tick {landed}; reacquiring Free Roam before camera writes resume.",
                tick,
                landed);
            if (!PauseReplayForCurrentOperation())
                throw new InvalidOperationException("Replay changed while the active Campath seek was landing.");
            try
            {
                ThrowIfQueuedActionLeaseExpired();
                await EnterFreeRoamForCurrentOperationAsync(cancellationToken).ConfigureAwait(false);
                await SynchronizeObserverOriginAfterSeekAsync(
                    path.Evaluate(landed),
                    cancellationToken,
                    ownershipEpoch).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new CampathStartException(
                    CampathStartFailure.FreeRoamReacquisitionFailed,
                    $"Free Roam could not be reacquired after seek: {ex.Message}",
                    ex);
            }

            var gateStatus = await SendHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
            gateStatus = await WaitForNativeAsync(
                client,
                status => status.Flags.HasFlag(InProcessStatusFlags.Resolved) &&
                          status.Flags.HasFlag(InProcessStatusFlags.ReplayGate),
                TimeSpan.FromSeconds(2),
                CampathStartFailure.FreeRoamReacquisitionFailed,
                "Native replay/Free Roam gate did not reopen after seek.",
                gateStatus,
                cancellationToken).ConfigureAwait(false);
            gateStatus = await WaitForFreshStrictFreeRoamCameraAsync(
                client,
                gateStatus,
                landed,
                "A fresh post-seek Free Roam camera frame was not observed before Campath recovery.",
                cancellationToken).ConfigureAwait(false);

            // The DLL deliberately retains the typed path while Free Roam is
            // absent. Re-send the bounded payload to establish a fresh accepted
            // sequence, then require a subsequent hook frame to prove ownership.
            Transition(CampathPlaybackState.TransferringPath,
                $"Refreshing {path.Keyframes.Count} typed keyframes after seek.", tick, landed);
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            var transferStatus = await client.SetCampathAsync(path, _endBehavior, cancellationToken)
                .ConfigureAwait(false);
            UpdateStatus(transferStatus);
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            if (!transferStatus.Flags.HasFlag(InProcessStatusFlags.CampathActive) ||
                !transferStatus.Flags.HasFlag(InProcessStatusFlags.HasSample))
                ThrowFromNative(transferStatus, CampathStartFailure.PathTransferFailed,
                    "The native backend rejected the Campath while recovering from seek.");

            Transition(CampathPlaybackState.ArmingNativeCamera,
                "Rearming terminal spectator-camera ownership after seek.", tick, landed);
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            var status = await client.EnableOverrideAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            if (!status.Flags.HasFlag(InProcessStatusFlags.OverrideRequested))
                ThrowFromNative(status, CampathStartFailure.NativePathNotArmed,
                    "The native backend did not rearm the Campath after seek.");

            Transition(CampathPlaybackState.WaitingForOwnership,
                "Waiting for the first validated post-seek camera frame.", tick, landed);
            status = await WaitForNativeAsync(
                client,
                candidate => candidate.OverrideActive &&
                             candidate.Flags.HasFlag(InProcessStatusFlags.CampathActive) &&
                             candidate.AppliedSequence >= transferStatus.AcceptedSequence,
                TimeSpan.FromSeconds(2),
                CampathStartFailure.CameraOwnershipRejected,
                "Native Campath did not reacquire the spectator camera after seek.",
                status,
                cancellationToken).ConfigureAwait(false);
            status = await EnsureManualCameraArmedUnderOverrideAsync(
                client,
                status,
                cancellationToken,
                ownershipEpoch).ConfigureAwait(false);

            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            Transition(CampathPlaybackState.Playing,
                $"Campath reacquired at landed tick {landed}.", tick, landed);
            return landed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (protectedManualSeek)
            {
                if (_client is { Connected: true } manualCleanupClient)
                    await BestEffortReleaseFullOverrideAsync(
                            manualCleanupClient,
                            "cancelled Free Camera seek cleanup")
                        .ConfigureAwait(false);
                _message = ManualCameraDesired
                    ? "Replay seek cancelled; SMVM Free Camera remains requested."
                    : "Replay seek cancelled; camera ownership was released fail-closed.";
                OnStatusChanged();
                throw;
            }
            if (!activeCampathSeek)
                throw;

            _campathPlaying = false;
            _holdingKeyframe = false;
            _activePath = null;
            if (_client is { Connected: true } cleanupClient)
                await BestEffortReleaseFullOverrideAsync(cleanupClient, "cancelled active-seek cleanup")
                    .ConfigureAwait(false);
            _playback.Cancel("Active Campath seek cancelled; camera ownership released.");
            _message = _playback.Status.Detail;
            RaiseCampathState();
            OnStatusChanged();
            throw;
        }
        catch (Exception ex) when (protectedManualSeek)
        {
            if (_client is { Connected: true } manualCleanupClient)
                await BestEffortReleaseFullOverrideAsync(
                        manualCleanupClient,
                        "failed Free Camera seek cleanup")
                    .ConfigureAwait(false);
            _message = ManualCameraDesired
                ? $"Replay seek failed; SMVM Free Camera remains requested: {ex.Message}"
                : $"Replay seek failed and camera ownership was released fail-closed: {ex.Message}";
            _log.Warn($"Native camera: {_message}");
            OnStatusChanged();
            throw new InvalidOperationException(_message, ex);
        }
        catch (CampathStartException ex)
        {
            _campathPlaying = false;
            _holdingKeyframe = false;
            _activePath = null;
            if (_client is { Connected: true } cleanupClient)
                await BestEffortReleaseFullOverrideAsync(cleanupClient, "failed active-seek cleanup")
                    .ConfigureAwait(false);
            _playback.Fail(ex.Failure, ex.Message);
            _message = ex.Message;
            _log.Warn($"Native camera: active Campath seek failed [{ex.Failure}] {ex.Message}");
            RaiseCampathState();
            OnStatusChanged();
            throw;
        }
        finally
        {
            if (restoreProtectedTransport)
                RestoreReplayTransportState(protectedTransportWasPaused);
            if (preservingManualCamera)
                ExitCameraTransfer();
            _operationGate.Release();
        }
    }

    private void RestoreReplayTransportState(bool? wasPaused)
    {
        if (wasPaused == true)
            _ = PauseReplayForCurrentOperation();
        else if (wasPaused == false)
            _ = PlayReplayForCurrentOperation();
    }

    private async Task<InProcessCameraStatus> RebaseManualCameraAtSampleAsync(
        NativeReplayCameraClient client,
        CameraSample baseline,
        CancellationToken cancellationToken,
        long ownershipEpoch)
    {
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        var transfer = await client.SetCameraSampleAsync(baseline, cancellationToken).ConfigureAwait(false);
        UpdateStatus(transfer);
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        if (!transfer.Flags.HasFlag(InProcessStatusFlags.HasSample))
            throw new InvalidOperationException(
                $"The native backend rejected the preserved Free Camera sample ({DescribeNativeStatus(transfer)})." );

        var beforeOverrideHooks = transfer.HookCalls;
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        var status = await client.EnableOverrideAsync(cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        if (!status.Flags.HasFlag(InProcessStatusFlags.OverrideRequested))
            throw new InvalidOperationException(
                $"The native backend did not arm the preserved Free Camera sample ({DescribeNativeStatus(status)})." );

        status = await WaitForNativeAsync(
            client,
            candidate => candidate.OverrideActive && candidate.CameraObserved &&
                         candidate.HookCalls > beforeOverrideHooks &&
                         candidate.AppliedSequence >= transfer.AcceptedSequence &&
                         SmvmSelfTestPolicy.SamplesMatch(baseline, candidate.Camera),
            TimeSpan.FromSeconds(2),
            CampathStartFailure.CameraOwnershipRejected,
            "The preserved Free Camera sample was not rendered after seek.",
            status,
            cancellationToken).ConfigureAwait(false);

        // Arm manual ownership under the exact temporary sample, then release
        // the override. The native handoff rebases manual motion from that
        // rendered sample without exposing a Deadlock-owned camera frame.
        await PublishFreshSmvmSnapshotAsync(client, required: true, cancellationToken).ConfigureAwait(false);
        status = await client.PrepareCameraObservationAsync(cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        status = await client.EnableManualCameraAsync(cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        if (!status.ManualCameraRequested)
            throw new InvalidOperationException(
                $"The native backend did not rearm SMVM Free Camera after seek ({DescribeNativeStatus(status)})." );

        var beforeManualHooks = status.HookCalls;
        status = await client.DisableOverrideAsync(cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        status = await WaitForManualCameraAsync(
            client,
            beforeManualHooks,
            status,
            cancellationToken).ConfigureAwait(false);
        if (!status.CameraObserved || !SmvmSelfTestPolicy.SamplesMatch(baseline, status.Camera))
            throw new InvalidOperationException(
                "SMVM Free Camera did not resume from the exact pre-seek rendered sample.");

        status = await client.ClearCampathAsync(cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        return status;
    }

    /// <summary>
    /// A demo seek rebuilds Deadlock's observer instance at an engine-owned
    /// position. Re-applying only SMVM's rendered sample would leave that hidden
    /// observer origin behind, so Source 2 could stream/cull from one location
    /// while the owner sees another. Drive the authoritative roaming target to
    /// the shot before native rendering resumes; the existing seek transfer flag
    /// keeps input held and the UPDATING TICKS prompt visible for the full settle.
    /// </summary>
    private async Task SynchronizeObserverOriginAfterSeekAsync(
        CameraSample renderedSample,
        CancellationToken cancellationToken,
        long ownershipEpoch)
    {
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        CameraState? settled;
        try
        {
            settled = await _camera.GoToPositionAsync(
                renderedSample.X,
                renderedSample.Y,
                renderedSample.Z,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A collision or transient getpos failure must not destroy a valid
            // replay landing. Preserve the existing exact rendered-camera
            // restore and surface the degraded visibility synchronization.
            _log.Warn(
                $"Native camera: post-seek observer-origin synchronization was unavailable: {ex.Message}");
            return;
        }

        // Do not let an explicit Free Camera exit that happened during the
        // engine glide fall through into a stale native re-arm.
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        if (settled?.ActiveTransform is not { } observer)
        {
            _log.Warn(
                "Native camera: Deadlock did not report an observer-origin landing after seek; " +
                "the rendered shot will still be restored.");
            return;
        }

        var dx = observer.X - renderedSample.X;
        var dy = observer.Y - renderedSample.Y;
        var dz = observer.Z - renderedSample.Z;
        var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (distance > 1.0)
        {
            _log.Warn(
                $"Native camera: post-seek observer origin settled {distance:0.##} units from " +
                "the rendered shot because of world collision; Source 2 visibility origin was still refreshed.");
        }
        else
        {
            _log.Info("Native camera: post-seek observer and rendered visibility origins synchronized.");
        }
    }

    private async Task<InProcessCameraStatus> EnsureManualCameraArmedUnderOverrideAsync(
        NativeReplayCameraClient client,
        InProcessCameraStatus status,
        CancellationToken cancellationToken,
        long ownershipEpoch)
    {
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        if (!ShouldArmManualCameraUnderOverride(ManualCameraDesired, status))
            return status;
        if (!CanResumeManualCamera(
                ManualCameraDesired,
                _controller.State,
                _controller.GameTickOffset,
                _camera.Selection.Mode))
            return status;

        await PublishFreshSmvmSnapshotAsync(client, required: false, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        if (!ManualCameraDesired)
            return status;
        status = await client.EnableManualCameraAsync(cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
        if (!status.ManualCameraRequested)
            throw new InvalidOperationException(
                $"The native backend did not preserve Free Camera intent beneath Campath " +
                $"({DescribeNativeStatus(status)}).");
        return status;
    }

    private async Task<InProcessCameraStatus> WaitForFreshStrictFreeRoamCameraAsync(
        NativeReplayCameraClient client,
        InProcessCameraStatus initialStatus,
        int? expectedTick,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var status = initialStatus;
        if (status.ManualCameraRequested || status.ManualCameraActive)
        {
            status = await client.DisableManualCameraAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
        }

        var beforeObservationHookCalls = status.HookCalls;
        status = await client.PrepareCameraObservationAsync(cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        return await WaitForNativeAsync(
            client,
            candidate => IsFreshStrictFreeRoamCameraFrame(
                candidate,
                beforeObservationHookCalls,
                expectedTick),
            TimeSpan.FromSeconds(2),
            CampathStartFailure.FreeRoamReacquisitionFailed,
            failureMessage,
            status,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishFreshSmvmSnapshotAsync(
        NativeReplayCameraClient client,
        bool required,
        CancellationToken cancellationToken)
    {
        var provider = SmvmSnapshotProvider;
        if (provider is null)
        {
            if (required)
                throw new InvalidOperationException(
                    "SMVM Free Camera state is unavailable until the editor snapshot is ready.");
            return;
        }

        var snapshot = provider();
        UpdateStatus(await client.UpdateSmvmSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false));
    }

    private async Task<InProcessCameraStatus> WaitForManualCameraAsync(
        NativeReplayCameraClient client,
        ulong beforeHookCalls,
        InProcessCameraStatus initialStatus,
        CancellationToken cancellationToken)
    {
        var status = initialStatus;
        if (IsFreshManualCameraFrame(status, beforeHookCalls))
            return status;

        var deadline = Stopwatch.GetTimestamp() +
                       (long)(TimeSpan.FromSeconds(2).TotalSeconds * Stopwatch.Frequency);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(15));
        while (ManualCameraDesired && Stopwatch.GetTimestamp() < deadline &&
               await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            if (IsFreshManualCameraFrame(status, beforeHookCalls))
                return status;
            if (ShouldAbortManualCameraWait(status))
                break;
        }

        throw new InvalidOperationException(
            $"SMVM Free Camera did not acquire a fresh game-thread frame ({DescribeNativeStatus(status)}).");
    }

    private async Task TryDisableManualCameraAsync(
        NativeReplayCameraClient client,
        CancellationToken cancellationToken)
    {
        if (!client.Connected)
            return;
        try
        {
            UpdateStatus(await client.DisableManualCameraAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _log.Warn($"Native camera: manual camera release could not be confirmed: {ex.Message}");
        }
    }

    private async Task BestEffortReleaseFullOverrideAsync(
        NativeReplayCameraClient client,
        string context)
    {
        var ownershipEpoch = CaptureOwnershipEpoch();
        using var cleanupTimeout = new CancellationTokenSource(BestEffortCleanupTimeout);
        var cleanupToken = cleanupTimeout.Token;
        var resumeManualCamera = CanResumeManualCameraForEpoch(ownershipEpoch);
        var releaseManualCamera = ShouldReleaseManualCameraOnStop(
            resumeManualCamera,
            ManualCameraDesired,
            _status?.Flags ?? InProcessStatusFlags.None);

        InProcessCameraStatus? status = null;
        var manualResumeArmed = false;
        if (releaseManualCamera)
        {
            try
            {
                status = await client.DisableManualCameraAsync(cleanupToken).ConfigureAwait(false);
                UpdateStatus(status);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                _log.Warn($"Native camera: {context} could not release manual camera: {ex.Message}");
            }
        }
        if (resumeManualCamera)
        {
            try
            {
                status = await client.GetStatusAsync(cleanupToken).ConfigureAwait(false);
                UpdateStatus(status);
                status = await EnsureManualCameraArmedUnderOverrideAsync(
                    client,
                    status,
                    cleanupToken,
                    ownershipEpoch).ConfigureAwait(false);
                manualResumeArmed = status.ManualCameraRequested;
            }
            catch (Exception ex)
            {
                _log.Warn($"Native camera: {context} could not preserve manual camera: {ex.Message}");
            }
        }

        if (!CanResumeManualCameraForEpoch(ownershipEpoch))
        {
            resumeManualCamera = false;
            manualResumeArmed = false;
            try
            {
                status = await DisableSupersededManualResumeAsync(
                    client, status, ownershipEpoch, cleanupToken, context).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.Warn($"Native camera: {context} explicit-exit cleanup timed out.");
            }
        }

        var beforeReleaseHookCalls = status?.HookCalls ?? 0;
        try
        {
            status = await client.DisableOverrideAsync(cleanupToken).ConfigureAwait(false);
            UpdateStatus(status);
            status = await client.ClearCampathAsync(cleanupToken).ConfigureAwait(false);
            UpdateStatus(status);
            if (resumeManualCamera && manualResumeArmed &&
                CanResumeManualCameraForEpoch(ownershipEpoch))
            {
                await WaitForManualCameraAsync(
                    client,
                    beforeReleaseHookCalls,
                    status,
                    cleanupToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            _log.Warn($"Native camera: {context} release failed: {ex.Message}");
        }

        if (!CanResumeManualCameraForEpoch(ownershipEpoch))
        {
            try
            {
                await DisableSupersededManualResumeAsync(
                    client, status, ownershipEpoch, cleanupToken, context).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.Warn($"Native camera: {context} final explicit-exit cleanup timed out.");
            }
        }
    }

    internal static bool IsFreshManualCameraFrame(
        InProcessCameraStatus status,
        ulong beforeHookCalls) =>
        status.ManualCameraRequested &&
        status.ManualCameraActive &&
        status.CameraObserved &&
        status.HookCalls > beforeHookCalls;

    internal static bool IsManualCameraEstablished(
        bool manualCameraDesired,
        InProcessCameraStatus? status) =>
        manualCameraDesired &&
        status is { ManualCameraRequested: true, ManualCameraActive: true, CameraObserved: true } &&
        status.Camera.IsValid;

    internal static bool TryGetManualSeekBaseline(
        bool manualCameraDesired,
        InProcessCameraStatus? status,
        out CameraSample baseline)
    {
        baseline = status?.Camera ?? default;
        return IsManualCameraEstablished(manualCameraDesired, status) && baseline.IsValid;
    }

    internal static bool IsFreshStrictFreeRoamCameraFrame(
        InProcessCameraStatus status,
        ulong beforeHookCalls,
        int? expectedTick) =>
        status.CameraObserved &&
        status.Camera.IsValid &&
        !status.ManualCameraRequested &&
        !status.ManualCameraActive &&
        // PrepareCameraObservation clears CameraObserved before returning. A
        // camera hook that was already in flight can increment HookCalls just
        // before that clear, then publish a newly validated observation with
        // the same counter value. Paused replays may not produce another hook,
        // so equality is fresh proof here; an older counter is still rejected.
        status.HookCalls >= beforeHookCalls &&
        (expectedTick is null ||
         // At accelerated replay rates Deadlock can advance the renderer a few
         // ticks after the controller reports the landed tick but before its
         // pause latch is visible to the camera hook. This remains a narrow
         // post-seek window; the actual seek landing contract stays at +/-2.
         Math.Abs((long)status.ReplayTick - expectedTick.Value) <= ObservationTickTolerance);

    internal static bool IsManualCameraGateReady(InProcessCameraStatus status) =>
        (status.State is InProcessBackendState.Connected or InProcessBackendState.Ready) &&
        status.Flags.HasFlag(InProcessStatusFlags.Resolved) &&
        status.Flags.HasFlag(InProcessStatusFlags.ReplayGate) &&
        status.Flags.HasFlag(InProcessStatusFlags.CommandLineReplay);

    internal static bool ShouldAbortManualCameraWait(InProcessCameraStatus status) =>
        !status.ManualCameraRequested || status.State == InProcessBackendState.Failed ||
        status.Error is InProcessErrorCode.ProtocolError or InProcessErrorCode.SignatureMissing or
            InProcessErrorCode.SignatureAmbiguous or InProcessErrorCode.HookInstallFailed or
            InProcessErrorCode.HookTargetMismatch or InProcessErrorCode.HookRuntimeInvalid or
            InProcessErrorCode.ReplayGateClosed or InProcessErrorCode.HeartbeatStale;

    internal static bool ShouldArmManualCameraUnderOverride(
        bool manualCameraDesired,
        InProcessCameraStatus status) =>
        manualCameraDesired && !status.ManualCameraRequested;

    internal static bool CanResumeManualCamera(
        bool manualCameraDesired,
        ReplayState replay,
        int? gameTickOffset,
        SpecCameraMode mode) =>
        manualCameraDesired && IsConfirmedReplay(replay) && gameTickOffset is not null &&
        mode == SpecCameraMode.FreeRoam;

    private bool CanResumeManualCameraForEpoch(long ownershipEpoch) =>
        IsOwnershipOperationCurrent(ownershipEpoch, CaptureOwnershipEpoch()) &&
        CanResumeManualCamera(
            ManualCameraDesired,
            _controller.State,
            _controller.GameTickOffset,
            _camera.Selection.Mode);

    private async Task<InProcessCameraStatus?> DisableSupersededManualResumeAsync(
        NativeReplayCameraClient client,
        InProcessCameraStatus? status,
        long ownershipEpoch,
        CancellationToken cancellationToken,
        string context)
    {
        if (CanResumeManualCameraForEpoch(ownershipEpoch))
            return status;
        try
        {
            status = await client.DisableManualCameraAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _log.Warn($"Native camera: {context} could not fail closed after an explicit exit: {ex.Message}");
        }
        return status;
    }

    internal static bool ShouldReleaseManualCameraOnStop(
        bool resumeManualCamera,
        bool manualCameraDesired,
        InProcessStatusFlags flags) =>
        !resumeManualCamera &&
        (manualCameraDesired ||
         flags.HasFlag(InProcessStatusFlags.ManualCameraRequested) ||
         flags.HasFlag(InProcessStatusFlags.ManualCameraActive));

    private async Task StopCampathCoreAsync(CancellationToken cancellationToken)
    {
        var ownershipEpoch = CaptureOwnershipEpoch();
        if (_playback.Status.State != CampathPlaybackState.Stopping)
            Transition(CampathPlaybackState.Stopping, "Releasing native Campath camera ownership.");
        var client = _client;
        var resumeManualCamera = CanResumeManualCameraForEpoch(ownershipEpoch);
        var releaseManualCamera = ShouldReleaseManualCameraOnStop(
            resumeManualCamera,
            ManualCameraDesired,
            _status?.Flags ?? InProcessStatusFlags.None);

        _campathPlaying = false;
        _holdingKeyframe = false;
        _activePath = null;
        var manualResumeArmed = false;
        var manualResumeConfirmed = false;
        if (client?.Connected == true)
        {
            InProcessCameraStatus? status = _status;
            if (releaseManualCamera)
            {
                try
                {
                    status = await client.DisableManualCameraAsync(cancellationToken).ConfigureAwait(false);
                    UpdateStatus(status);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    _log.Warn($"Native camera: manual camera release failed during Stop: {ex.Message}");
                }
            }
            if (resumeManualCamera)
            {
                try
                {
                    status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    UpdateStatus(status);
                    status = await EnsureManualCameraArmedUnderOverrideAsync(
                        client,
                        status,
                        cancellationToken,
                        ownershipEpoch).ConfigureAwait(false);
                    manualResumeArmed = status.ManualCameraRequested;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn($"Native camera: manual camera could not be armed before Campath release: {ex.Message}");
                }
            }

            if (!CanResumeManualCameraForEpoch(ownershipEpoch))
            {
                resumeManualCamera = false;
                manualResumeArmed = false;
                status = await DisableSupersededManualResumeAsync(
                    client,
                    status,
                    ownershipEpoch,
                    cancellationToken,
                    "Campath Stop").ConfigureAwait(false);
            }

            var beforeReleaseHookCalls = status?.HookCalls ?? 0;
            try
            {
                status = await client.DisableOverrideAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus(status);
                status = await client.ClearCampathAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus(status);
                if (resumeManualCamera && manualResumeArmed &&
                    CanResumeManualCameraForEpoch(ownershipEpoch))
                {
                    status = await WaitForManualCameraAsync(
                        client,
                        beforeReleaseHookCalls,
                        status,
                        cancellationToken).ConfigureAwait(false);
                    manualResumeConfirmed = true;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _log.Warn($"Native camera: release failed: {ex.Message}");
            }

            if (!CanResumeManualCameraForEpoch(ownershipEpoch))
            {
                resumeManualCamera = false;
                manualResumeConfirmed = false;
                await DisableSupersededManualResumeAsync(
                    client,
                    status,
                    ownershipEpoch,
                    cancellationToken,
                    "Campath Stop").ConfigureAwait(false);
            }
        }

        _message = resumeManualCamera
            ? manualResumeConfirmed
                ? "Campath stopped; SMVM Free Camera resumed from the final shot."
                : "Campath stopped; Free Camera remains requested but its resumed frame is not yet confirmed."
            : ManualCameraDesired
                ? "Campath stopped; SMVM Free Camera remains requested. Press F2 to reacquire it."
            : "Campath stopped; Deadlock owns the Free Roam camera.";
        Transition(CampathPlaybackState.Stopped, _message);
        OnStatusChanged();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        await RunMonitorLoopAsync(
            async token =>
            {
                var observedClient = Volatile.Read(ref _client);
                if (ShouldRetireDisconnectedClient(
                        observedClient is not null,
                        observedClient?.Connected == true))
                {
                    await HandleMonitorFailureAsync(
                        new IOException("The native presentation pipe was retired."),
                        token).ConfigureAwait(false);
                }
                if (_client?.Connected != true)
                    await TryConnectAsync(token).ConfigureAwait(false);
                else
                {
                    try
                    {
                        await SendHeartbeatAsync(_client, token).ConfigureAwait(false);
                        if (SmvmSnapshotProvider?.Invoke() is { } snapshot)
                        {
                            using var snapshotLease = BeginSmvmSnapshotPublicationLease(snapshot);
                            UpdateStatus(await _client.UpdateSmvmSnapshotAsync(snapshot, token)
                                .ConfigureAwait(false));
                        }
                    }
                    catch (Exception ex) when (ShouldRetryMonitorAfterReplayLeaseChange(
                               ex,
                               _client?.Connected == true))
                    {
                        // Map activation and same-file replay generation changes
                        // can supersede a fully read request. The pipe framing is
                        // still healthy; retry with the new lease instead of
                        // expiring the native snapshot and dropping the UI/input.
                    }
                }
            },
            HandleMonitorFailureAsync,
            token => timer.WaitForNextTickAsync(token),
            cancellationToken).ConfigureAwait(false);
    }

    internal static bool ShouldRetireDisconnectedClient(
        bool clientPresent,
        bool connected) =>
        clientPresent && !connected;

    internal static bool ShouldRetryMonitorAfterReplayLeaseChange(
        Exception exception,
        bool clientConnected) =>
        clientConnected && exception is InvalidOperationException &&
        string.Equals(
            exception.Message,
            QueuedActionLeaseExpiredMessage,
            StringComparison.Ordinal);

    internal static async Task RunMonitorLoopAsync(
        Func<CancellationToken, Task> runIteration,
        Func<Exception, CancellationToken, Task> handleFailure,
        Func<CancellationToken, ValueTask<bool>> waitForNextIteration,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await runIteration(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await handleFailure(ex, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (!await waitForNextIteration(cancellationToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleMonitorFailureAsync(Exception ex, CancellationToken _)
    {
        if (ex is StaleConnectionLeaseException)
        {
            _log.Info($"Native camera: ignored retired heartbeat lease: {ex.Message}");
            return;
        }

        var preserveManualIntent = ManualCameraDesired;
        var campathInterrupted = _campathPlaying || _holdingKeyframe ||
                                 _playback.Status.State == CampathPlaybackState.Playing;
        _campathPlaying = false;
        _holdingKeyframe = false;
        _activePath = null;
        if (_status is { } status)
        {
            _status = status with
            {
                State = InProcessBackendState.Failed,
                Error = InProcessErrorCode.ProtocolError,
                Flags = InProcessStatusFlags.None,
            };
        }

        var failedConnectionEpoch = ConnectionEpoch;
        Interlocked.Increment(ref _connectionEpoch);
        var client = Interlocked.Exchange(ref _client, null);
        RetireAutomaticRecoveryClaimForConnection(failedConnectionEpoch);
        if (client is not null)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeException) when (disposeException is IOException or InvalidOperationException)
            {
                _log.Warn($"Native camera: failed client cleanup after disconnect: {disposeException.Message}");
            }
        }

        _message = preserveManualIntent
            ? $"SMVM Free Camera was interrupted by a native backend failure: {ex.Message} " +
              "Retrying the backend; automatic recovery will resume after it reconnects."
            : $"Native backend failed: {ex.Message} Retrying while replay playback remains available.";
        if (campathInterrupted)
        {
            _playback.Fail(CampathStartFailure.NativeBackendDisconnected,
                "Campath stopped because the native backend disconnected.");
            RaiseCampathState();
        }
        _log.Warn(_message);
        OnStatusChanged();
    }

    private async Task TryConnectAsync(CancellationToken cancellationToken)
    {
        var replay = _controller.State;
        if (!IsConfirmedReplay(replay) || _controller.GameTickOffset is null)
            return;
        if (SmvmSnapshotProvider is null)
            return;
        if (!File.Exists(_dllPath))
        {
            _message = $"Native backend unavailable: {Path.GetFileName(_dllPath)} is missing.";
            OnStatusChanged();
            return;
        }

        using var process = FindDeadlockProcess();
        if (process is null)
            return;

        _message = "Native backend loading…";
        _status = new InProcessCameraStatus(
            InProcessBackendState.Loading, InProcessErrorCode.None, InProcessStatusFlags.None,
            process.Id, 0, 0, 0, -1, default,
            SmvmRendererBackend.None, SmvmRendererError.None, SmvmOverlayFlags.None, 0, SmvmAction.None);
        OnStatusChanged();

        var load = NativeReplayModuleLoader.LoadForReplay(process.Id, _dllPath);
        if (!load.Success)
        {
            _status = _status with { State = InProcessBackendState.Failed };
            _message = load.Message;
            _log.Warn($"Native camera: {load.Message}");
            OnStatusChanged();
            return;
        }

        var client = new NativeReplayCameraClient();
        client.ExpectedReplaySessionGenerationProvider =
            ExpectedReplaySessionGenerationForCurrentRequest;
        var connectionEpoch = Interlocked.Increment(ref _connectionEpoch);
        client.BindSessionConnectionEpoch(connectionEpoch);
        client.SmvmActionReceived += OnClientSmvmActionReceived;
        try
        {
            var status = await client.ConnectAsync(process.Id, TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            var snapshotProvider = SmvmSnapshotProvider ??
                throw new InvalidOperationException(
                    "The SMVM editor snapshot is not ready for native connection publication.");
            // Native host-loss restoration must be armed before managed code
            // can observe Connected and issue any forward recording-profile
            // command. The first periodic monitor snapshot is too late for
            // this publication boundary.
            var initialSnapshot = snapshotProvider();
            using (BeginSmvmSnapshotPublicationLease(initialSnapshot))
            {
                status = await client.UpdateSmvmSnapshotAsync(initialSnapshot, cancellationToken)
                    .ConfigureAwait(false);
                if (!RunQueuedEffectForCurrentOperation(() =>
                    {
                        _client = client;
                        UpdateStatusCore(status);
                        return true;
                    }))
                {
                    throw new InvalidOperationException(
                        "Replay changed before the native connection could be published.");
                }
            }
            _message = status.Error == InProcessErrorCode.None
                ? ManualCameraDesired
                    ? "Native backend reconnected; automatic SMVM Free Camera recovery is starting."
                    : "Native backend connected; camera override is armed only in Free Roam."
                : $"Native backend connected with error: {status.Error}.";
            _log.Info($"Native camera: {load.Message} Protocol v{InProcessProtocol.Version} connected.");
            await SendHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.CompareExchange(ref _client, null, client);
            client.SmvmActionReceived -= OnClientSmvmActionReceived;
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<InProcessCameraStatus> SendHeartbeatAsync(
        NativeReplayCameraClient client,
        CancellationToken cancellationToken)
    {
        var heartbeatReplayLease = _queuedActionContext.Value is null
            ? CaptureCurrentReplayCommandLease()
            : null;
        using var heartbeatActionLease = heartbeatReplayLease is { } replayLease
            ? BeginQueuedActionLease(static () => true, replayLease)
            : null;
        ThrowIfQueuedActionLeaseExpired();
        var replay = _controller.State;
        var replayActive = IsConfirmedReplay(replay);
        var freeRoam = _camera.Selection.Mode == SpecCameraMode.FreeRoam;
        var playbackInterrupted = _playback.Status.State != CampathPlaybackState.Idle &&
                                  !_playback.Status.IsTerminal;
        var hadManualCamera = ManualCameraDesired ||
                              _status?.ManualCameraRequested == true ||
                              _status?.ManualCameraActive == true;
        var status = await client.HeartbeatAsync(
            replayActive,
            freeRoam,
            replay.CurrentTick ?? -1,
            _controller.GameTickOffset ?? -1,
            cancellationToken).ConfigureAwait(false);
        var responseReplayLease =
            _queuedActionContext.Value?.ReplayLease ?? heartbeatReplayLease;

        void ApplyHeartbeatResponse()
        {
        if (!IsHeartbeatResponseCurrent(
                ReferenceEquals(client, Volatile.Read(ref _client)),
                client.Connected,
                status.SourceConnectionEpoch,
                ConnectionEpoch))
        {
            throw new StaleConnectionLeaseException(
                "A native heartbeat completed after its connection lease was retired.");
        }
        UpdateStatus(status);
        if (!IsHeartbeatResponseCurrent(
                ReferenceEquals(client, Volatile.Read(ref _client)),
                client.Connected,
                status.SourceConnectionEpoch,
                ConnectionEpoch))
        {
            throw new StaleConnectionLeaseException(
                "A native heartbeat connection lease was retired while its status was being applied.");
        }
        var currentReplay = _controller.State;
        var currentGameTickOffset = _controller.GameTickOffset;
        var currentReplayActive = IsConfirmedReplay(currentReplay);
        if (ShouldQueueManualCameraRecoveryAfterHeartbeat(
                currentReplay,
                currentGameTickOffset,
                ManualCameraDesired,
                _campathPlaying,
                _holdingKeyframe,
                Volatile.Read(ref _cameraTransferDepth) > 0,
                Volatile.Read(ref _manualRecoveryClaim) != 0,
                status))
        {
            QueueAutomaticManualCameraRecovery(
                "Replay telemetry recovered; automatic SMVM Free Camera recovery is starting.",
                releaseManualCamera: false,
                releaseFullOverride: false,
                forceInternalFreeRoam: true);
        }
        if (_campathPlaying && _activePath is { } activePath &&
            _playback.Status.State == CampathPlaybackState.Playing)
        {
            if (_endBehavior == CampathEndBehavior.StopAndRelease &&
                status.CampathCompleted)
            {
                _campathPlaying = false;
                Transition(CampathPlaybackState.Completed,
                    $"Campath reached final tick {activePath.Keyframes[^1].DemoTick}.",
                    activePath.Keyframes[^1].DemoTick,
                    status.ReplayTick);
                if (CaptureCurrentReplayCommandLease() is { } completionLease)
                    _ = CompleteAndReleaseAsync(completionLease);
            }
            else if (!status.OverrideActive &&
                     _playback.Status.State == CampathPlaybackState.Playing &&
                     _camera.Selection.Mode == SpecCameraMode.FreeRoam)
            {
                var failure = MapNativeFailure(status, CampathStartFailure.CameraOwnershipRejected);
                _playback.Fail(failure,
                    $"Campath lost native camera ownership ({DescribeNativeStatus(status)})." );
                RaiseCampathState();
                if (CaptureCurrentReplayCommandLease() is { } ownershipLossLease)
                    _ = StopAfterOwnershipLossAsync(_playback.Status.Detail, ownershipLossLease);
            }
        }
        if (_holdingKeyframe && currentReplayActive && !status.OverrideActive &&
            _camera.Selection.Mode == SpecCameraMode.FreeRoam &&
            Interlocked.Exchange(ref _ownershipCleanupQueued, 1) == 0)
        {
            if (CaptureCurrentReplayCommandLease() is { } heldKeyframeLease)
                _ = StopAfterOwnershipLossAsync(
                    "Held keyframe lost native camera ownership.",
                    heldKeyframeLease);
            else
                Interlocked.Exchange(ref _ownershipCleanupQueued, 0);
        }
        if (HasConfirmedReplayEnded(currentReplay) &&
            (hadManualCamera || _campathPlaying || _holdingKeyframe || playbackInterrupted))
        {
            CancelManualCameraIntent();
            _campathPlaying = false;
            _holdingKeyframe = false;
            _activePath = null;
            if (playbackInterrupted)
            {
                _playback.Fail(
                    CampathStartFailure.ReplayUnavailable,
                    "Camera path stopped because replay playback ended.");
                RaiseCampathState();
                _message = _playback.Status.Detail;
            }
            else
            {
                _message = "Native camera ownership released because replay playback ended.";
            }
            OnStatusChanged();
        }
        else if (!freeRoam && Volatile.Read(ref _cameraTransferDepth) == 0)
        {
            OnSelectionChanged(this, EventArgs.Empty);
        }
        }

        if (responseReplayLease is { } exactReplayLease)
        {
            if (!_controller.RunIfCurrent(exactReplayLease, ApplyHeartbeatResponse))
                throw new InvalidOperationException(
                    "The replay session changed while a native heartbeat was in flight.");
        }
        else
        {
            ApplyHeartbeatResponse();
        }
        return status;
    }

    internal static bool HasConfirmedReplayEnded(ReplayState replay) =>
        replay.Connected &&
        !string.IsNullOrWhiteSpace(replay.ReplayName) &&
        replay.CurrentTick is { } currentTick &&
        replay.TotalTicks is { } totalTicks &&
        currentTick >= totalTicks;

    internal static bool ShouldQueueManualCameraRecoveryAfterHeartbeat(
        ReplayState replay,
        int? gameTickOffset,
        bool manualCameraDesired,
        bool campathPlaying,
        bool holdingKeyframe,
        bool cameraTransferInProgress,
        bool recoveryAlreadyQueued,
        InProcessCameraStatus status) =>
        IsConfirmedReplay(replay) && gameTickOffset is not null && manualCameraDesired &&
        !campathPlaying && !holdingKeyframe &&
        !cameraTransferInProgress && !recoveryAlreadyQueued &&
        !status.ManualCameraRequested && !status.ManualCameraActive;

    internal static bool IsHeartbeatResponseCurrent(
        bool samePublishedClient,
        bool clientConnected,
        long responseConnectionEpoch,
        long currentConnectionEpoch) =>
        samePublishedClient &&
        IsConnectionLeaseCurrent(
            responseConnectionEpoch,
            currentConnectionEpoch,
            clientConnected);

    private void ThrowIfQueuedActionLeaseExpired()
    {
        var context = _queuedActionContext.Value;
        if (context is null)
            return;
        if (!context.StillCurrent() ||
            context.ReplayLease is { } replayLease &&
            !_controller.IsReplayCommandLeaseCurrent(replayLease))
        {
            throw new InvalidOperationException(QueuedActionLeaseExpiredMessage);
        }
    }

    private long ExpectedReplaySessionGenerationForCurrentRequest()
    {
        var context = _queuedActionContext.Value;
        if (context is null)
            return 0;

        // NativeReplayCameraClient calls this only after acquiring its request
        // gate and immediately before serializing the request. Revalidate the
        // captured managed lease at that final send boundary, then stamp the
        // same immutable replay generation for the native server's fence.
        ThrowIfQueuedActionLeaseExpired();
        return context.ReplayLease?.ReplaySessionGeneration ?? 0;
    }

    private IDisposable BeginSmvmSnapshotPublicationLease(SmvmSnapshot snapshot)
    {
        var captured = _controller.CaptureReplayTelemetrySnapshot();
        var expectedConnectionGeneration = captured.ConnectionGeneration;
        var expectedReplaySessionGeneration = snapshot.ReplaySessionGeneration;
        var replay = captured.State;
        var replayLease = expectedReplaySessionGeneration > 0 &&
                          replay.ReplaySessionGeneration == expectedReplaySessionGeneration &&
                          replay.Connected && replay.CurrentTick is not null &&
                          !string.IsNullOrWhiteSpace(replay.ReplayName)
            ? new ReplayCommandLease(
                expectedConnectionGeneration,
                expectedReplaySessionGeneration,
                replay.ReplayName.Trim())
            : (ReplayCommandLease?)null;

        bool StillCurrent()
        {
            var current = _controller.CaptureReplayTelemetrySnapshot();
            return expectedConnectionGeneration > 0 &&
                   current.ConnectionGeneration == expectedConnectionGeneration &&
                   current.State.ReplaySessionGeneration == expectedReplaySessionGeneration;
        }

        return BeginQueuedActionLease(StillCurrent, replayLease);
    }

    private Task<CameraState?> EnterFreeRoamForCurrentOperationAsync(
        CancellationToken cancellationToken) =>
        _camera.EnterFreeRoamIfCurrentAsync(RunCameraEffectForCurrentOperation, cancellationToken);

    private bool RunCameraEffectForCurrentOperation(Action effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        var context = _queuedActionContext.Value;
        if (context is null)
        {
            effect();
            return true;
        }
        if (!context.StillCurrent())
            return false;
        return context.ReplayLease is { } replayLease
            ? _controller.RunIfCurrent(replayLease, effect)
            : RunUnchecked(effect);
    }

    private bool RunQueuedEffectForCurrentOperation(Func<bool> effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        var context = _queuedActionContext.Value;
        if (context is null)
            return effect();
        if (!context.StillCurrent())
            return false;
        if (context.ReplayLease is not { } replayLease)
            return effect();

        var result = false;
        return _controller.RunIfCurrent(replayLease, () => result = effect()) && result;
    }

    private static bool RunUnchecked(Action effect)
    {
        effect();
        return true;
    }

    private ReplaySeekLease CaptureReplaySeekLease()
    {
        if (_queuedActionContext.Value?.ReplayLease is { } queuedLease)
        {
            ThrowIfQueuedActionLeaseExpired();
            return new ReplaySeekLease(
                queuedLease.ConnectionGeneration,
                queuedLease.ReplaySessionGeneration,
                queuedLease.ReplayName.Trim());
        }

        var captured = _controller.CaptureReplayTelemetrySnapshot();
        return TryCreateReplaySeekLease(captured.ConnectionGeneration, captured.State) ??
               throw new InvalidOperationException(
                   "Replay playback must be confirmed before seeking a Campath.");
    }

    private ReplayCommandLease? CaptureCurrentReplayCommandLease()
    {
        var captured = _controller.CaptureReplayTelemetrySnapshot();
        var replay = captured.State;
        if (captured.ConnectionGeneration <= 0 ||
            replay.ReplaySessionGeneration <= 0 ||
            !replay.Connected ||
            replay.CurrentTick is null ||
            string.IsNullOrWhiteSpace(replay.ReplayName))
        {
            return null;
        }

        // A terminal replay still owns an authoritative identity. Heartbeat
        // cleanup must remain fenced to it while a replacement demo loads.
        return new ReplayCommandLease(
            captured.ConnectionGeneration,
            replay.ReplaySessionGeneration,
            replay.ReplayName.Trim());
    }

    private bool PauseReplayForCurrentOperation()
    {
        var lease = _queuedActionContext.Value?.ReplayLease;
        if (lease is null)
        {
            _controller.Pause();
            return true;
        }
        return _controller.PauseIfCurrent(lease.Value);
    }

    private bool PlayReplayForCurrentOperation()
    {
        var lease = _queuedActionContext.Value?.ReplayLease;
        if (lease is null)
        {
            _controller.Play();
            return true;
        }
        return _controller.PlayIfCurrent(lease.Value);
    }

    private bool StepReplayForCurrentOperation(int count = 1)
    {
        var lease = _queuedActionContext.Value?.ReplayLease;
        if (lease is null)
        {
            _controller.StepTick(count);
            return true;
        }
        return _controller.StepTickIfCurrent(lease.Value, count);
    }

    private async Task<int> SeekReplayAndWaitForLandingAsync(
        int tick,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SeekToPathStartAsync(tick, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"Deadlock did not land at requested tick {tick} within 15 seconds.",
                ex);
        }
    }

    private async Task<int> SeekToPathStartAsync(int tick, CancellationToken cancellationToken)
    {
        var lease = CaptureReplaySeekLease();
        var captured = _controller.CaptureReplayTelemetrySnapshot();

        if (TryGetLandedReplayTick(captured.State, tick, out var landed))
        {
            ThrowIfReplaySeekLeaseExpired(lease);
            return landed;
        }

        var landing = new ReplaySeekLandingAwaiter(tick, lease);
        void OnCompleted(object? sender, int completedTick)
        {
            var current = _controller.CaptureReplayTelemetrySnapshot();
            landing.ObserveSeekCompleted(
                completedTick,
                current.ConnectionGeneration,
                current.State);
        }

        void OnStateChanged(object? sender, ReplayState state)
        {
            var current = _controller.CaptureReplayTelemetrySnapshot();
            landing.ObserveAuthoritativeState(
                state,
                current.ConnectionGeneration,
                current.State);
        }

        _controller.SeekCompleted += OnCompleted;
        _controller.StateChanged += OnStateChanged;
        try
        {
            // Close the small read/subscribe race before issuing a command. A
            // current-tick Go To is already complete and Deadlock may emit no
            // seek-finished console line for that no-op.
            var current = _controller.CaptureReplayTelemetrySnapshot();
            if (!IsReplaySeekLeaseCurrent(
                    lease,
                    current.ConnectionGeneration,
                    current.State))
            {
                landing.Expire();
                return await landing.Completion.ConfigureAwait(false);
            }
            if (TryGetLandedReplayTick(current.State, tick, out landed))
                return landed;

            if (!_controller.SeekToTickIfCurrent(
                    tick,
                    lease.ConnectionGeneration,
                    lease.ReplaySessionGeneration,
                    lease.ReplayName))
            {
                landing.Expire();
                return await landing.Completion.ConfigureAwait(false);
            }
            current = _controller.CaptureReplayTelemetrySnapshot();
            landing.ObserveAuthoritativeState(
                current.State,
                current.ConnectionGeneration,
                current.State);
            landed = await landing.Completion
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
            ThrowIfReplaySeekLeaseExpired(lease);
            return landed;
        }
        finally
        {
            _controller.SeekCompleted -= OnCompleted;
            _controller.StateChanged -= OnStateChanged;
        }
    }

    internal static bool TryGetLandedReplayTick(ReplayState state, int requestedTick, out int landedTick)
    {
        landedTick = state.CurrentTick ?? -1;
        return IsConfirmedReplay(state) && IsTickWithinTolerance(landedTick, requestedTick);
    }

    internal static bool IsTickWithinTolerance(int candidateTick, int requestedTick) =>
        Math.Abs((long)candidateTick - requestedTick) <= SeekTickTolerance;

    internal static ReplaySeekLease? TryCreateReplaySeekLease(
        long connectionGeneration,
        ReplayState state)
    {
        if (connectionGeneration <= 0 || state.ReplaySessionGeneration <= 0 || !IsConfirmedReplay(state))
            return null;

        return new ReplaySeekLease(
            connectionGeneration,
            state.ReplaySessionGeneration,
            state.ReplayName!.Trim());
    }

    internal static bool IsReplaySeekLeaseCurrent(
        ReplaySeekLease lease,
        long connectionGeneration,
        ReplayState state) =>
        connectionGeneration == lease.ConnectionGeneration &&
        state.ReplaySessionGeneration == lease.ReplaySessionGeneration &&
        IsConfirmedReplay(state) &&
        string.Equals(
            state.ReplayName!.Trim(),
            lease.ReplayName,
            StringComparison.OrdinalIgnoreCase);

    private void ThrowIfReplaySeekLeaseExpired(ReplaySeekLease lease)
    {
        var current = _controller.CaptureReplayTelemetrySnapshot();
        if (!IsReplaySeekLeaseCurrent(
                lease,
                current.ConnectionGeneration,
                current.State))
        {
            throw new InvalidOperationException(
                ReplaySeekLandingAwaiter.ReplaySessionChangedMessage);
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _cameraTransferDepth) > 0)
            return;

        var flags = _status?.Flags ?? InProcessStatusFlags.None;
        if (!ShouldReleaseCameraForPovLoss(
                _camera.Selection.Mode,
                _campathPlaying,
                _holdingKeyframe,
                ManualCameraDesired,
                flags))
            return;

        var releaseManualCamera = flags.HasFlag(InProcessStatusFlags.ManualCameraRequested) ||
                                  flags.HasFlag(InProcessStatusFlags.ManualCameraActive) ||
                                  flags.HasFlag(InProcessStatusFlags.RollOverrideActive);
        var releaseFullOverride = HasFullCameraOwnershipIntent(_campathPlaying, _holdingKeyframe, flags);
        var message = ManualCameraDesired
            ? "SMVM Free Camera was interrupted because Deadlock changed POV. " +
              "Camera writes were released; automatic recovery is starting."
            : "Native camera ownership released because POV mode changed.";
        if (ManualCameraDesired)
        {
            QueueAutomaticManualCameraRecovery(
                message,
                releaseManualCamera,
                releaseFullOverride);
        }
        else if (Interlocked.Exchange(ref _ownershipCleanupQueued, 1) == 0)
        {
            if (CaptureCurrentReplayCommandLease() is { } cleanupReplayLease)
            {
                _ = StopAfterPovLossAsync(
                    message,
                    releaseManualCamera,
                    releaseFullOverride,
                    automaticRecoveryClaimed: false,
                    ownershipEpoch: CaptureOwnershipEpoch(),
                    cleanupReplayLease: cleanupReplayLease);
            }
            else
            {
                Interlocked.Exchange(ref _ownershipCleanupQueued, 0);
            }
        }
    }

    private void QueueAutomaticManualCameraRecovery(
        string message,
        bool releaseManualCamera,
        bool releaseFullOverride,
        bool forceInternalFreeRoam = false)
    {
        var ownershipEpoch = CaptureOwnershipEpoch();
        var replaySnapshot = _controller.CaptureReplayTelemetrySnapshot();
        var replaySeekLease = TryCreateReplaySeekLease(
            replaySnapshot.ConnectionGeneration,
            replaySnapshot.State);
        if (replaySeekLease is not { } capturedReplay)
            return;
        var replayLease = new ReplayCommandLease(
            capturedReplay.ConnectionGeneration,
            capturedReplay.ReplaySessionGeneration,
            capturedReplay.ReplayName);
        long recoveryClaim;
        lock (_manualIntentGate)
        {
            if (!CanQueueAutomaticManualCameraRecovery(
                    _manualCameraDesired,
                    _manualRecoveryClaim != 0))
                return;
            recoveryClaim = NextManualRecoveryClaimLocked();
            _manualRecoveryClaim = recoveryClaim;
            _manualRecoveryClaimConnectionEpoch = ConnectionEpoch;
        }

        _ = StopAfterPovLossAsync(
            message,
            releaseManualCamera,
            releaseFullOverride,
            automaticRecoveryClaimed: true,
            ownershipEpoch: ownershipEpoch,
            forceInternalFreeRoam: forceInternalFreeRoam,
            automaticRecoveryClaim: recoveryClaim,
            automaticRecoveryReplayLease: replayLease);
    }

    private async Task StopAfterPovLossAsync(
        string message,
        bool releaseManualCamera,
        bool releaseFullOverride,
        bool automaticRecoveryClaimed,
        long ownershipEpoch,
        bool forceInternalFreeRoam = false,
        long automaticRecoveryClaim = 0,
        ReplayCommandLease? automaticRecoveryReplayLease = null,
        ReplayCommandLease? cleanupReplayLease = null)
    {
        var operationReplayLease = automaticRecoveryReplayLease ?? cleanupReplayLease;
        using var recoveryActionLease = operationReplayLease is { } replayLease
            ? BeginQueuedActionLease(
                () => !automaticRecoveryClaimed ||
                      IsOwnershipOperationCurrent(ownershipEpoch, CaptureOwnershipEpoch()) &&
                      IsAutomaticRecoveryClaimCurrent(automaticRecoveryClaim),
                replayLease)
            : null;
        var automaticRecoveryAttempted = false;
        NativeReplayCameraClient? automaticRecoveryClient = null;
        var automaticRecoveryConnectionEpoch = 0L;
        using var recoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        if (automaticRecoveryClaimed)
            recoveryTimeout.CancelAfter(AutomaticRecoveryTimeout);
        var recoveryToken = recoveryTimeout.Token;
        try
        {
            var recoveredAutomatically = false;
            await _operationGate.WaitAsync(recoveryToken).ConfigureAwait(false);
            EnterCameraTransfer();
            try
            {
                ThrowIfQueuedActionLeaseExpired();
                var client = _client;
                if (automaticRecoveryClaimed &&
                    !IsAutomaticRecoveryClaimCurrent(automaticRecoveryClaim))
                    return;
                if (releaseManualCamera && client?.Connected == true)
                    await TryDisableManualCameraAsync(client, recoveryToken).ConfigureAwait(false);
                if (releaseFullOverride)
                    await StopCampathCoreAsync(recoveryToken).ConfigureAwait(false);

                if (automaticRecoveryClaimed && CanAutoRecoverManualCamera(
                        ManualCameraDesired,
                        client?.Connected == true,
                        _controller.State,
                        _controller.GameTickOffset) &&
                    !CampathCameraOwned)
                {
                    automaticRecoveryAttempted = true;
                    automaticRecoveryClient = client;
                    automaticRecoveryConnectionEpoch = ConnectionEpoch;
                    ScopeAutomaticRecoveryClaimToConnection(
                        automaticRecoveryClaim,
                        automaticRecoveryConnectionEpoch);
                    try
                    {
                        await AcquireManualCameraCoreAsync(
                            client!,
                            recoveryToken,
                            forceInternalFreeRoam,
                            ownershipEpoch).ConfigureAwait(false);
                        message = "SMVM Free Camera automatically recovered after Deadlock changed POV.";
                        recoveredAutomatically = true;
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        using var cleanupTimeout = new CancellationTokenSource(BestEffortCleanupTimeout);
                        try
                        {
                            await TryDisableManualCameraAsync(client!, cleanupTimeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
                        {
                            _log.Warn("Native camera: automatic recovery cleanup timed out; the pipe was retired.");
                        }
                        message += ManualCameraDesired
                            ? $" Automatic recovery failed: {ex.Message} Free Camera intent remains; press F2 to retry."
                            : " Automatic recovery was superseded by an explicit Free Camera exit.";
                    }
                }
            }
            finally
            {
                ExitCameraTransfer();
                _operationGate.Release();
            }

            _message = message;
            if (recoveredAutomatically)
                _log.Info($"Native camera: {message}");
            else
                _log.Warn($"Native camera: {message}");
            OnStatusChanged();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (automaticRecoveryClaimed && recoveryTimeout.IsCancellationRequested)
        {
            _message = "Automatic SMVM Free Camera recovery timed out safely. " +
                       "Free Camera intent remains; press F2 to retry.";
            _log.Warn($"Native camera: {_message}");
            OnStatusChanged();
        }
        catch (Exception ex)
        {
            _message = $"{message} Native release could not be confirmed: {ex.Message}";
            _log.Warn($"Native camera: {_message}");
            OnStatusChanged();
        }
        finally
        {
            if (!automaticRecoveryClaimed)
                Interlocked.Exchange(ref _ownershipCleanupQueued, 0);
            else if (ManualCameraDesired &&
                     IsOwnershipOperationCurrent(ownershipEpoch, CaptureOwnershipEpoch()) &&
                     ShouldReleaseAutomaticRecoveryClaim(
                         automaticRecoveryAttempted,
                         automaticRecoveryClient is not null &&
                         IsHeartbeatResponseCurrent(
                             ReferenceEquals(automaticRecoveryClient, Volatile.Read(ref _client)),
                             automaticRecoveryClient.Connected,
                             automaticRecoveryConnectionEpoch,
                             ConnectionEpoch),
                         CanAutoRecoverManualCamera(
                             ManualCameraDesired,
                             _client?.Connected == true,
                             _controller.State,
                             _controller.GameTickOffset)))
                ReleaseAutomaticRecoveryClaim(automaticRecoveryClaim);
        }
    }

    internal static bool ShouldReleaseAutomaticRecoveryClaim(
        bool recoveryAttempted,
        bool attemptedConnectionLeaseCurrent,
        bool currentReplayCanRecover) =>
        !recoveryAttempted || !attemptedConnectionLeaseCurrent || !currentReplayCanRecover;

    internal static bool IsAutomaticRecoveryClaimOwnedByConnection(
        long claim,
        long claimConnectionEpoch,
        long connectionEpoch) =>
        claim > 0 && claimConnectionEpoch > 0 && claimConnectionEpoch == connectionEpoch;

    internal static bool IsSameAutomaticRecoveryClaim(long currentClaim, long candidateClaim) =>
        candidateClaim > 0 && currentClaim == candidateClaim;

    internal static bool CanQueueAutomaticManualCameraRecovery(
        bool manualCameraDesired,
        bool recoveryAlreadyQueued) =>
        manualCameraDesired && !recoveryAlreadyQueued;

    internal static bool CanAutoRecoverManualCamera(
        bool manualCameraDesired,
        bool nativeConnected,
        ReplayState replay,
        int? gameTickOffset) =>
        manualCameraDesired && nativeConnected && IsConfirmedReplay(replay) && gameTickOffset is not null;

    private async Task StopAfterOwnershipLossAsync(
        string message,
        ReplayCommandLease replayLease)
    {
        using var actionLease = BeginQueuedActionLease(static () => true, replayLease);
        try
        {
            await StopCampathAsync(_stop.Token).ConfigureAwait(false);
            _message = message;
            _log.Warn($"Native camera: {message}");
            OnStatusChanged();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _message = $"{message} Native release could not be confirmed: {ex.Message}";
            _log.Warn($"Native camera: {_message}");
            OnStatusChanged();
        }
        finally
        {
            Interlocked.Exchange(ref _ownershipCleanupQueued, 0);
        }
    }

    internal static bool ShouldReleaseCameraForPovLoss(
        SpecCameraMode mode,
        bool campathPlaying,
        bool holdingKeyframe,
        bool manualCameraDesired,
        InProcessStatusFlags flags) =>
        mode != SpecCameraMode.FreeRoam &&
        (manualCameraDesired ||
         HasFullCameraOwnershipIntent(campathPlaying, holdingKeyframe, flags) ||
         flags.HasFlag(InProcessStatusFlags.ManualCameraRequested) ||
         flags.HasFlag(InProcessStatusFlags.ManualCameraActive) ||
         flags.HasFlag(InProcessStatusFlags.RollOverrideActive));

    internal static bool HasCameraOwnershipIntent(
        bool campathPlaying,
        bool holdingKeyframe,
        bool manualCameraDesired,
        InProcessStatusFlags flags) =>
        HasFullCameraOwnershipIntent(campathPlaying, holdingKeyframe, flags) ||
        manualCameraDesired ||
        flags.HasFlag(InProcessStatusFlags.ManualCameraRequested) ||
        flags.HasFlag(InProcessStatusFlags.ManualCameraActive);

    internal static bool HasFullCameraOwnershipIntent(
        bool campathPlaying,
        bool holdingKeyframe,
        InProcessStatusFlags flags) =>
        campathPlaying || holdingKeyframe ||
        flags.HasFlag(InProcessStatusFlags.OverrideRequested) ||
        flags.HasFlag(InProcessStatusFlags.OverrideActive);

    internal static bool HasNativeCameraResourcesToRelease(InProcessStatusFlags flags) =>
        HasCameraOwnershipIntent(
            campathPlaying: false,
            holdingKeyframe: false,
            manualCameraDesired: false,
            flags) ||
        flags.HasFlag(InProcessStatusFlags.CampathActive) ||
        flags.HasFlag(InProcessStatusFlags.RollOverrideActive);

    internal static bool ShouldReportManualCameraInterruption(
        bool manualCameraDesired,
        InProcessCameraStatus? previous,
        InProcessCameraStatus current,
        bool cameraTransferInProgress)
    {
        if (!manualCameraDesired || cameraTransferInProgress ||
            previous is not { ManualCameraRequested: true } and not { ManualCameraActive: true } ||
            current.ManualCameraRequested || current.ManualCameraActive)
            return false;
        return true;
    }

    private void SetManualCameraDesired(bool desired)
    {
        var changed = false;
        lock (_manualIntentGate)
        {
            changed = _manualCameraDesired != desired;
            Volatile.Write(ref _manualCameraDesired, desired);
            if (!desired)
                BlockAutomaticRecoveryLocked();
        }
        if (changed)
            OnStatusChanged();
    }

    private bool TrySetManualCameraDesiredForEpoch(long ownershipEpoch)
    {
        var changed = false;
        lock (_manualIntentGate)
        {
            if (!IsOwnershipOperationCurrent(ownershipEpoch, _explicitExitEpoch))
                return false;
            changed = !_manualCameraDesired;
            Volatile.Write(ref _manualCameraDesired, true);
            // A current explicit F2 activation supersedes the scope-zero exit
            // blocker. If this acquisition loses its client, the next live
            // connection may claim automatic recovery normally.
            _manualRecoveryClaim = 0;
            _manualRecoveryClaimConnectionEpoch = 0;
        }
        if (changed)
            OnStatusChanged();
        return true;
    }

    private bool CompleteManualCameraAcquisitionForEpoch(
        long ownershipEpoch,
        InProcessCameraStatus status)
    {
        lock (_manualIntentGate)
        {
            if (!IsOwnershipOperationCurrent(ownershipEpoch, _explicitExitEpoch) ||
                !IsManualCameraEstablished(_manualCameraDesired, status))
                return false;
            _manualRecoveryClaim = 0;
            _manualRecoveryClaimConnectionEpoch = 0;
            return true;
        }
    }

    private void MarkManualCameraEstablished(InProcessCameraStatus status)
    {
        lock (_manualIntentGate)
        {
            if (IsManualCameraEstablished(_manualCameraDesired, status))
            {
                _manualRecoveryClaim = 0;
                _manualRecoveryClaimConnectionEpoch = 0;
            }
        }
    }

    private long NextManualRecoveryClaimLocked()
    {
        _nextManualRecoveryClaim++;
        if (_nextManualRecoveryClaim <= 0)
            _nextManualRecoveryClaim = 1;
        return _nextManualRecoveryClaim;
    }

    private void BlockAutomaticRecoveryLocked()
    {
        _manualRecoveryClaim = NextManualRecoveryClaimLocked();
        _manualRecoveryClaimConnectionEpoch = 0;
    }

    private void ReleaseAutomaticRecoveryClaim(long recoveryClaim)
    {
        if (recoveryClaim <= 0)
            return;
        lock (_manualIntentGate)
        {
            if (_manualRecoveryClaim != recoveryClaim)
                return;
            _manualRecoveryClaim = 0;
            _manualRecoveryClaimConnectionEpoch = 0;
        }
    }

    private bool IsAutomaticRecoveryClaimCurrent(long recoveryClaim)
    {
        if (recoveryClaim <= 0)
            return false;
        lock (_manualIntentGate)
            return IsSameAutomaticRecoveryClaim(_manualRecoveryClaim, recoveryClaim);
    }

    private void ScopeAutomaticRecoveryClaimToConnection(
        long recoveryClaim,
        long connectionEpoch)
    {
        if (recoveryClaim <= 0 || connectionEpoch <= 0)
            return;
        lock (_manualIntentGate)
        {
            if (_manualRecoveryClaim == recoveryClaim)
                _manualRecoveryClaimConnectionEpoch = connectionEpoch;
        }
    }

    private void RetireAutomaticRecoveryClaimForConnection(long connectionEpoch)
    {
        lock (_manualIntentGate)
        {
            if (!IsAutomaticRecoveryClaimOwnedByConnection(
                    _manualRecoveryClaim,
                    _manualRecoveryClaimConnectionEpoch,
                    connectionEpoch))
                return;
            _manualRecoveryClaim = 0;
            _manualRecoveryClaimConnectionEpoch = 0;
        }
    }

    private void EnterCameraTransfer() => Interlocked.Increment(ref _cameraTransferDepth);

    private void ExitCameraTransfer()
    {
        if (Interlocked.Decrement(ref _cameraTransferDepth) == 0 &&
            _camera.Selection.Mode != SpecCameraMode.FreeRoam)
            OnSelectionChanged(this, EventArgs.Empty);
    }

    private async Task CompleteAndReleaseAsync(ReplayCommandLease replayLease)
    {
        using var actionLease = BeginQueuedActionLease(static () => true, replayLease);
        try
        {
            await StopCampathAsync(_stop.Token).ConfigureAwait(false);
            _message = ManualCameraActive
                ? "Campath completed; SMVM Free Camera resumed from the final shot."
                : ManualCameraDesired
                    ? "Campath completed; Free Camera remains requested but its resumed frame is not yet confirmed."
                    : "Campath completed and released the camera; replay playback is unchanged.";
            OnStatusChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"Native camera: completion release failed: {ex.Message}");
        }
    }

    private async Task<InProcessCameraStatus> CaptureFreshSelfTestFrameAsync(
        NativeReplayCameraClient client,
        InProcessCameraStatus initial,
        CancellationToken cancellationToken)
    {
        var beforeHookCalls = initial.HookCalls;
        var prepared = await client.PrepareCameraObservationAsync(cancellationToken).ConfigureAwait(false);
        UpdateStatus(prepared);
        return await PollSelfTestStatusAsync(
            client,
            status => status.CameraObserved && status.Camera.IsValid && status.HookCalls > beforeHookCalls,
            prepared,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<InProcessCameraStatus> PollSelfTestStatusAsync(
        NativeReplayCameraClient client,
        Func<InProcessCameraStatus, bool> success,
        InProcessCameraStatus initial,
        CancellationToken cancellationToken)
    {
        var status = initial;
        if (success(status))
            return status;
        var deadline = Stopwatch.GetTimestamp() + (long)(TimeSpan.FromSeconds(2).TotalSeconds * Stopwatch.Frequency);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(15));
        while (Stopwatch.GetTimestamp() < deadline &&
               await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            if (success(status) || status.State == InProcessBackendState.Failed ||
                status.Error is InProcessErrorCode.ProtocolError or InProcessErrorCode.SignatureMissing or
                    InProcessErrorCode.SignatureAmbiguous or InProcessErrorCode.HookInstallFailed or
                    InProcessErrorCode.HookTargetMismatch or InProcessErrorCode.HookRuntimeInvalid or
                    InProcessErrorCode.ReplayGateClosed or InProcessErrorCode.ObserverNotRoaming or
                    InProcessErrorCode.HeartbeatStale)
                break;
        }
        return status;
    }

    private async Task<string?> RestoreAfterSelfTestAsync(
        NativeReplayCameraClient client,
        SmvmSelfTestKind kind,
        SmvmSelfTestRestorationPolicy restoration,
        long ownershipEpoch)
    {
        using var restorationTimeout = new CancellationTokenSource(SelfTestRestorationTimeout);
        var restorationToken = restorationTimeout.Token;
        try
        {
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            PublishSelfTest(kind, SmvmSelfTestStage.RestoringCamera,
                "Restoring the exact baseline camera sample.", restoration.Baseline);
            var transfer = await client.SetCameraSampleAsync(restoration.Baseline, restorationToken)
                .ConfigureAwait(false);
            UpdateStatus(transfer);
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            var beforeHookCalls = transfer.HookCalls;
            var armed = await client.EnableOverrideAsync(restorationToken).ConfigureAwait(false);
            UpdateStatus(armed);
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            var restored = await PollSelfTestStatusAsync(
                client,
                status => status.OverrideActive && status.CameraObserved && status.HookCalls > beforeHookCalls &&
                          SmvmSelfTestPolicy.SamplesMatch(restoration.Baseline, status.Camera),
                armed,
                restorationToken).ConfigureAwait(false);
            ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
            if (!restored.OverrideActive || !restored.CameraObserved ||
                !SmvmSelfTestPolicy.SamplesMatch(restoration.Baseline, restored.Camera))
                throw new InvalidOperationException(
                    $"Baseline camera readback could not be confirmed ({DescribeNativeStatus(restored)})." );

            PublishSelfTest(kind, SmvmSelfTestStage.RestoringOwnership,
                "Restoring the camera ownership state that existed before the self-test.");

            if (restoration.RestoreManualCamera)
            {
                // Arm manual ownership beneath the still-active baseline override.
                // DisableOverride then rebases manual from the exact applied sample,
                // avoiding a Deadlock-owned recompute frame between the two modes.
                ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                if (!TrySetManualCameraDesiredForEpoch(ownershipEpoch))
                    throw new InvalidOperationException(
                        "Self-test ownership restoration was superseded by an explicit exit.");
                await PublishFreshSmvmSnapshotAsync(client, required: true, restorationToken)
                    .ConfigureAwait(false);
                var prepared = await client.PrepareCameraObservationAsync(restorationToken).ConfigureAwait(false);
                UpdateStatus(prepared);
                ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                var requested = await client.EnableManualCameraAsync(restorationToken).ConfigureAwait(false);
                UpdateStatus(requested);
                ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                if (!requested.ManualCameraRequested)
                    throw new InvalidOperationException("The prior Free Camera request could not be rearmed.");
                var beforeManualHooks = requested.HookCalls;
                var released = await client.DisableOverrideAsync(restorationToken).ConfigureAwait(false);
                UpdateStatus(released);
                var manualRestored = await WaitForManualCameraAsync(
                        client, beforeManualHooks, released, restorationToken)
                    .ConfigureAwait(false);
                if (!manualRestored.CameraObserved ||
                    !SmvmSelfTestPolicy.SamplesMatch(restoration.Baseline, manualRestored.Camera))
                    throw new InvalidOperationException(
                        "The restored Free Camera did not rebase from the authoritative baseline sample.");
                UpdateStatus(await client.ClearCampathAsync(restorationToken).ConfigureAwait(false));
            }
            else
            {
                SetManualCameraDesired(false);
                UpdateStatus(await client.DisableOverrideAsync(restorationToken).ConfigureAwait(false));
                UpdateStatus(await client.ClearCampathAsync(restorationToken).ConfigureAwait(false));
            }
            if (restoration.RestoreRollOverride)
            {
                ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                var rollStatus = await client.SetRollOverrideAsync(
                    restoration.Baseline.Roll, restorationToken).ConfigureAwait(false);
                UpdateStatus(rollStatus);
                ThrowIfOwnershipOperationSuperseded(ownershipEpoch);
                var beforeRollHooks = rollStatus.HookCalls;
                bool RollOwnershipRestored(InProcessCameraStatus status) =>
                    status.RollOverrideActive && status.CameraObserved &&
                    (!restoration.RestoreManualCamera ||
                     status.ManualCameraRequested && status.ManualCameraActive) &&
                    status.HookCalls > beforeRollHooks &&
                    Math.Abs(ShortestAngleDelta(status.Camera.Roll, restoration.Baseline.Roll)) <= 0.025;
                var verifiedRoll = await PollSelfTestStatusAsync(
                    client,
                    RollOwnershipRestored,
                    rollStatus,
                    restorationToken).ConfigureAwait(false);
                if (!RollOwnershipRestored(verifiedRoll))
                    throw new InvalidOperationException("The prior roll override could not be restored.");
            }
            return null;
        }
        catch (Exception ex)
        {
            if (!restoration.RestoreManualCamera ||
                !TrySetManualCameraDesiredForEpoch(ownershipEpoch))
                SetManualCameraDesired(false);
            using var cleanupTimeout = new CancellationTokenSource(SelfTestEmergencyCleanupTimeout);
            try { await TryDisableManualCameraAsync(client, cleanupTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _log.Warn("Native camera: self-test manual cleanup timed out; the pipe was retired.");
            }
            try { UpdateStatus(await client.DisableOverrideAsync(cleanupTimeout.Token).ConfigureAwait(false)); }
            catch (Exception cleanupException) when (cleanupException is IOException or InvalidOperationException or
                                                       OperationCanceledException)
            {
                _log.Warn($"Native camera: self-test override cleanup failed: {cleanupException.Message}");
            }
            try { UpdateStatus(await client.ClearCampathAsync(cleanupTimeout.Token).ConfigureAwait(false)); }
            catch (Exception cleanupException) when (cleanupException is IOException or InvalidOperationException or
                                                       OperationCanceledException)
            {
                _log.Warn($"Native camera: self-test Campath cleanup failed: {cleanupException.Message}");
            }
            return $"Self-test restoration failed closed: {ex.Message}";
        }
    }

    private void PublishSelfTest(
        SmvmSelfTestKind kind,
        SmvmSelfTestStage stage,
        string detail,
        CameraSample? expected = null,
        CameraSample? actual = null) =>
        PublishSelfTest(new SmvmSelfTestResult(kind, stage, SmvmSelfTestFailure.None, detail, expected, actual));

    private void PublishSelfTest(SmvmSelfTestResult result)
    {
        _selfTestStatus = result;
        _message = result.Detail;
        _log.Info($"Native camera: {result.Kind} self-test {result.Stage}: {result.Detail}");
        RaiseWithoutQueuedActionContext(() => SelfTestStateChanged?.Invoke(this, result));
        OnStatusChanged();
    }

    private SmvmSelfTestResult CompleteSelfTestSuccess(
        SmvmSelfTestKind kind,
        string detail,
        CameraSample? expected = null,
        CameraSample? actual = null)
    {
        var result = new SmvmSelfTestResult(
            kind, SmvmSelfTestStage.Completed, SmvmSelfTestFailure.None, detail, expected, actual);
        return result;
    }

    private SmvmSelfTestResult CompleteSelfTestFailure(
        SmvmSelfTestKind kind,
        SmvmSelfTestFailure failure,
        string detail,
        CameraSample? expected = null,
        CameraSample? actual = null,
        SmvmSelfTestStage stage = SmvmSelfTestStage.Failed)
    {
        var result = new SmvmSelfTestResult(kind, stage, failure, detail, expected, actual);
        return result;
    }

    private static string SelfTestFailureDetail(SmvmSelfTestFailure failure) => failure switch
    {
        SmvmSelfTestFailure.NativeBackendDisconnected => "Native backend is disconnected.",
        SmvmSelfTestFailure.ReplayUnavailable => "A confirmed replay and calibrated replay tick are required.",
        SmvmSelfTestFailure.NotInFreeRoam => "Camera self-tests require Free Roam and never change POV automatically.",
        SmvmSelfTestFailure.CameraReadbackUnavailable => "A fresh authoritative camera frame is unavailable.",
        SmvmSelfTestFailure.CameraAlreadyOwned => "Stop the active Campath or held keyframe before running a self-test.",
        SmvmSelfTestFailure.InvalidPath => "Campath self-test requires 2-128 valid, uniquely timed keyframes.",
        SmvmSelfTestFailure.CurrentTickOutsidePath => "Current replay tick is outside the editor path; no seek was performed.",
        _ => $"Self-test unavailable: {failure}.",
    };

    private async Task<InProcessCameraStatus> WaitForNativeAsync(
        NativeReplayCameraClient client,
        Func<InProcessCameraStatus, bool> success,
        TimeSpan timeout,
        CampathStartFailure fallbackFailure,
        string failureMessage,
        InProcessCameraStatus initialStatus,
        CancellationToken cancellationToken)
    {
        var status = initialStatus;
        if (success(status))
            return status;

        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(15));
        while (Stopwatch.GetTimestamp() < deadline &&
               await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            if (success(status))
                return status;
            if (status.State == InProcessBackendState.Failed ||
                status.Error is InProcessErrorCode.ProtocolError or InProcessErrorCode.SignatureMissing or
                    InProcessErrorCode.SignatureAmbiguous or InProcessErrorCode.HookInstallFailed or
                    InProcessErrorCode.HookTargetMismatch or InProcessErrorCode.HookRuntimeInvalid)
                break;
        }

        ThrowFromNative(status, fallbackFailure, failureMessage);
        throw new UnreachableException();
    }

    [DoesNotReturn]
    private void ThrowFromNative(
        InProcessCameraStatus status,
        CampathStartFailure fallbackFailure,
        string message)
    {
        var failure = MapNativeFailure(status, fallbackFailure);
        ThrowStartFailure(failure, $"{message} ({DescribeNativeStatus(status)})");
    }

    [DoesNotReturn]
    private void ThrowStartFailure(CampathStartFailure failure, string message, Exception? inner = null)
    {
        if (failure == CampathStartFailure.None)
            failure = CampathStartFailure.UnexpectedFailure;
        _playback.Fail(failure, message);
        _message = message;
        _log.Warn($"Native camera: Campath start failed [{failure}] {message}");
        RaiseCampathState();
        OnStatusChanged();
        throw new CampathStartException(failure, message, inner);
    }

    private static CampathStartFailure MapNativeFailure(
        InProcessCameraStatus status,
        CampathStartFailure fallback) => status.Error switch
    {
        InProcessErrorCode.None => fallback == CampathStartFailure.None ? CampathStartFailure.UnexpectedFailure : fallback,
        InProcessErrorCode.WrongProcess or InProcessErrorCode.ReplayLaunchRequired or
            InProcessErrorCode.ReplayGateClosed or InProcessErrorCode.ReplayClockUnavailable =>
            CampathStartFailure.ReplayUnavailable,
        InProcessErrorCode.ClientModuleMissing or InProcessErrorCode.SignatureMissing or
            InProcessErrorCode.SignatureAmbiguous or InProcessErrorCode.HookTargetMismatch or
            InProcessErrorCode.HookInstallFailed => CampathStartFailure.SignaturesUnavailable,
        InProcessErrorCode.CameraUnavailable => CampathStartFailure.CameraPointerInvalid,
        InProcessErrorCode.ProtocolError => CampathStartFailure.ProtocolMismatch,
        InProcessErrorCode.InvalidSample => CampathStartFailure.PathTransferFailed,
        InProcessErrorCode.ObserverNotRoaming => CampathStartFailure.NotInFreeRoam,
        InProcessErrorCode.HeartbeatStale => CampathStartFailure.ReplayHeartbeatStale,
        InProcessErrorCode.HookRuntimeInvalid => CampathStartFailure.ObserverChainInvalid,
        _ => fallback == CampathStartFailure.None ? CampathStartFailure.UnexpectedFailure : fallback,
    };

    private static string DescribeNativeStatus(InProcessCameraStatus status) =>
        $"state={status.State}, error={status.Error}, flags={status.Flags}, " +
        $"accepted={status.AcceptedSequence}, applied={status.AppliedSequence}, " +
        $"hookCalls={status.HookCalls}, tick={status.ReplayTick}";

    private void Transition(
        CampathPlaybackState state,
        string detail,
        long? requestedTick = null,
        long? actualTick = null)
    {
        _playback.Transition(state, detail, requestedTick, actualTick);
        _message = detail;
        _log.Info($"Native camera: Campath state {state}: {detail}");
        RaiseCampathState();
        OnStatusChanged();
    }

    private void RaiseCampathState() =>
        RaiseWithoutQueuedActionContext(() => CampathStateChanged?.Invoke(this, _playback.Status));

    private void UpdateStatus(InProcessCameraStatus status)
    {
        var context = _queuedActionContext.Value;
        if (context is null)
        {
            UpdateStatusCore(status);
            return;
        }
        if (!context.StillCurrent())
            throw new InvalidOperationException(
                "The queued SMVM action expired before its native response could be applied.");
        if (context.ReplayLease is { } replayLease)
        {
            if (!_controller.RunIfCurrent(replayLease, () => UpdateStatusCore(status)))
                throw new InvalidOperationException(
                    "The replay session changed before a native response could be applied.");
            return;
        }
        UpdateStatusCore(status);
    }

    private void UpdateStatusCore(InProcessCameraStatus status)
    {
        // A request can finish after its pipe was retired. Never let that stale
        // response replace current status or deliver an action under a newer
        // connection lease.
        if (status.SourceConnectionEpoch > 0 &&
            status.SourceConnectionEpoch != ConnectionEpoch)
            return;

        var previous = _status;
        var manualCameraInterrupted = ShouldReportManualCameraInterruption(
            ManualCameraDesired,
            previous,
            status,
            Volatile.Read(ref _cameraTransferDepth) > 0);
        _status = status;
        MarkManualCameraEstablished(status);
        if (manualCameraInterrupted)
        {
            var canRecoverNow = CanAutoRecoverManualCamera(
                ManualCameraDesired,
                _client?.Connected == true,
                _controller.State,
                _controller.GameTickOffset);
            _message = $"SMVM Free Camera was interrupted by the native backend " +
                       $"({DescribeNativeStatus(status)}). Camera writes are fail-closed; " +
                       (canRecoverNow
                           ? "automatic recovery is starting."
                           : "automatic recovery is waiting for confirmed replay telemetry.");
            _log.Warn($"Native camera: {_message}");
            if (canRecoverNow)
            {
                QueueAutomaticManualCameraRecovery(
                    _message,
                    releaseManualCamera: true,
                    releaseFullOverride: false);
            }
        }
        var lifecycle = status.OverlayFlags &
            (SmvmOverlayFlags.HookInstalled | SmvmOverlayFlags.PresentObserved | SmvmOverlayFlags.Ready |
             SmvmOverlayFlags.MenuOpen | SmvmOverlayFlags.CleanView |
             SmvmOverlayFlags.ManualPointerActive | SmvmOverlayFlags.ManualMouseObserved |
             SmvmOverlayFlags.ManualPointerRequested | SmvmOverlayFlags.KeyboardReady |
             SmvmOverlayFlags.RelativeMouseReady | SmvmOverlayFlags.RawInputReady |
             SmvmOverlayFlags.CursorReady | SmvmOverlayFlags.ForegroundReady |
             SmvmOverlayFlags.WindowProcedureReady | SmvmOverlayFlags.EngineInputReady |
             SmvmOverlayFlags.FallbackMouseObserved);
        if (status.RendererBackend != _loggedRendererBackend ||
            status.RendererError != _loggedRendererError || lifecycle != _loggedRendererLifecycle)
        {
            _loggedRendererBackend = status.RendererBackend;
            _loggedRendererError = status.RendererError;
            _loggedRendererLifecycle = lifecycle;
            var detail = $"SMVM renderer: backend={status.RendererBackend}, error={status.RendererError}, " +
                         $"flags={lifecycle}, frame={status.OverlayFrameMicroseconds} us.";
            if (status.RendererError == SmvmRendererError.None)
                _log.Info(detail);
            else
                _log.Warn(detail);
        }
        OnStatusChanged();
    }

    private void OnClientSmvmActionReceived(object? sender, SmvmActionDispatch dispatch)
    {
        // The client stamps provenance before releasing its request gate, so
        // this event is FIFO for a connection. A retired client can still be
        // unwinding; its old epoch is rejected here and again by the host lease.
        if (dispatch.ConnectionEpoch != ConnectionEpoch)
            return;
        try
        {
            RaiseWithoutQueuedActionContext(() => SmvmActionReceived?.Invoke(this, dispatch));
        }
        catch (Exception ex)
        {
            // A host callback failure is not a framing failure and must not
            // invalidate an otherwise healthy native pipe. F9 still has its
            // independent native inverse path.
            _log.Warn($"Native camera: SMVM action dispatch failed: {ex.Message}");
        }
    }

    public async Task PublishEditorCampathAsync(
        IReadOnlyList<CampathKeyframe> keyframes,
        CampathInterpolationMode interpolation,
        CampathEasingMode easing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        var publicationReplayLease = _queuedActionContext.Value is null
            ? CaptureCurrentReplayCommandLease() ??
              throw new InvalidOperationException(
                  "A confirmed replay identity is required to publish the editor path.")
            : (ReplayCommandLease?)null;
        using var publicationActionLease = publicationReplayLease is { } replayLease
            ? BeginQueuedActionLease(static () => true, replayLease)
            : null;
        ThrowIfQueuedActionLeaseExpired();
        var client = Volatile.Read(ref _client);
        var connectionEpoch = ConnectionEpoch;
        if (!IsPublicationResponseCurrent(
                ReferenceEquals(client, Volatile.Read(ref _client)),
                client?.Connected == true,
                connectionEpoch,
                connectionEpoch,
                ConnectionEpoch))
        {
            throw new StaleConnectionLeaseException(
                "The native editor-path connection was retired before publication started.");
        }
        InProcessCameraStatus status;
        if (keyframes.Count == 0)
        {
            status = await client!.ClearEditorCampathAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            status = await client!.SetEditorCampathAsync(keyframes, interpolation, easing, cancellationToken)
                .ConfigureAwait(false);
        }
        ThrowIfPublicationResponseRetired(
            client,
            connectionEpoch,
            status,
            "The native editor-path connection was retired during publication.");
        UpdateStatus(status);
        ThrowIfPublicationResponseRetired(
            client,
            connectionEpoch,
            status,
            "The native editor-path connection was retired while its response was being applied.");
    }

    /// <summary>Publishes the bounded saved-path picker list for the internal Load UI.</summary>
    public async Task PublishCampathDocumentsAsync(
        IReadOnlyList<CampathDocumentInfo> documents,
        CampathReplayIdentifier? currentReplay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var publicationReplayLease = _queuedActionContext.Value is null
            ? CaptureCurrentReplayCommandLease() ??
              throw new InvalidOperationException(
                  "A confirmed replay identity is required to publish Campath documents.")
            : (ReplayCommandLease?)null;
        using var publicationActionLease = publicationReplayLease is { } replayLease
            ? BeginQueuedActionLease(static () => true, replayLease)
            : null;
        ThrowIfQueuedActionLeaseExpired();
        var client = Volatile.Read(ref _client);
        var connectionEpoch = ConnectionEpoch;
        if (!IsPublicationResponseCurrent(
                ReferenceEquals(client, Volatile.Read(ref _client)),
                client?.Connected == true,
                connectionEpoch,
                connectionEpoch,
                ConnectionEpoch))
        {
            throw new StaleConnectionLeaseException(
                "The native path-list connection was retired before publication started.");
        }
        var status = await client!.SetCampathDocumentsAsync(
            documents.Count > InProcessProtocol.MaxCampathDocuments
                ? documents.Take(InProcessProtocol.MaxCampathDocuments).ToArray()
                : documents,
            currentReplay,
            cancellationToken).ConfigureAwait(false);
        ThrowIfPublicationResponseRetired(
            client,
            connectionEpoch,
            status,
            "The native path-list connection was retired during publication.");
        UpdateStatus(status);
        ThrowIfPublicationResponseRetired(
            client,
            connectionEpoch,
            status,
            "The native path-list connection was retired while its response was being applied.");
    }

    private void ThrowIfPublicationResponseRetired(
        NativeReplayCameraClient client,
        long capturedConnectionEpoch,
        InProcessCameraStatus status,
        string message)
    {
        if (!IsPublicationResponseCurrent(
                ReferenceEquals(client, Volatile.Read(ref _client)),
                client.Connected,
                capturedConnectionEpoch,
                status.SourceConnectionEpoch,
                ConnectionEpoch))
        {
            throw new StaleConnectionLeaseException(message);
        }
    }

    internal static bool IsPublicationResponseCurrent(
        bool samePublishedClient,
        bool clientConnected,
        long capturedConnectionEpoch,
        long responseConnectionEpoch,
        long currentConnectionEpoch) =>
        samePublishedClient &&
        clientConnected &&
        capturedConnectionEpoch > 0 &&
        responseConnectionEpoch == capturedConnectionEpoch &&
        capturedConnectionEpoch == currentConnectionEpoch;

    private void OnStatusChanged() =>
        RaiseWithoutQueuedActionContext(() => StatusChanged?.Invoke(this, EventArgs.Empty));

    private void RaiseWithoutQueuedActionContext(Action raise)
    {
        var previous = _queuedActionContext.Value;
        _queuedActionContext.Value = null;
        try
        {
            raise();
        }
        finally
        {
            _queuedActionContext.Value = previous;
        }
    }

    private static bool IsConfirmedReplay(ReplayState replay) =>
        replay.Connected && !string.IsNullOrWhiteSpace(replay.ReplayName) && replay.CurrentTick is not null &&
        (replay.TotalTicks is null || replay.CurrentTick < replay.TotalTicks);

    private static Process? FindDeadlockProcess() =>
        Process.GetProcessesByName("deadlock").FirstOrDefault() ??
        Process.GetProcessesByName("project8").FirstOrDefault();

    private sealed class StaleConnectionLeaseException(string message) : IOException(message);

    public async ValueTask DisposeAsync()
    {
        _camera.SelectionChanged -= OnSelectionChanged;
        CancelManualCameraIntent();
        SmvmSnapshotProvider = null;
        _stop.Cancel();
        try { await _monitorTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

        var operationGateEntered = false;
        using var shutdownTimeout = new CancellationTokenSource(BestEffortCleanupTimeout);
        try
        {
            try
            {
                await _operationGate.WaitAsync(shutdownTimeout.Token).ConfigureAwait(false);
                operationGateEntered = true;
            }
            catch (OperationCanceledException) when (shutdownTimeout.IsCancellationRequested)
            {
                _log.Warn(
                    "Native camera: shutdown operation gate timed out; retiring the pipe so host-loss recovery can restore Deadlock.");
            }

            Interlocked.Increment(ref _connectionEpoch);
            var client = Interlocked.Exchange(ref _client, null);
            if (client is not null)
            {
                try
                {
                    if (operationGateEntered && client.Connected)
                    {
                        UpdateStatus(await client.DisableManualCameraAsync(shutdownTimeout.Token)
                            .ConfigureAwait(false));
                        UpdateStatus(await client.DisableOverrideAsync(shutdownTimeout.Token)
                            .ConfigureAwait(false));
                        UpdateStatus(await client.ShutdownAsync(shutdownTimeout.Token)
                            .ConfigureAwait(false));
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or
                                           OperationCanceledException)
                {
                    _log.Warn($"Native camera: shutdown response unavailable: {ex.Message}");
                }
                finally
                {
                    // Closing the pipe is the bounded fallback. Native host-loss
                    // recovery owns the exact inverse if graceful shutdown did
                    // not acknowledge within the shared budget.
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }

            _campathPlaying = false;
            _holdingKeyframe = false;
            _activePath = null;
            if (_status is { } status)
            {
                _status = status with
                {
                    Flags = status.Flags &
                            ~(InProcessStatusFlags.OverrideRequested |
                              InProcessStatusFlags.OverrideActive |
                              InProcessStatusFlags.CampathActive |
                              InProcessStatusFlags.ManualCameraRequested |
                              InProcessStatusFlags.ManualCameraActive),
                };
            }
        }
        finally
        {
            if (operationGateEntered)
            {
                _operationGate.Release();
                _operationGate.Dispose();
                _stop.Dispose();
            }
        }
    }

    private static double ShortestAngleDelta(double from, double to)
    {
        var delta = (to - from) % 360.0;
        if (delta > 180.0) delta -= 360.0;
        if (delta < -180.0) delta += 360.0;
        return delta;
    }

    private sealed record QueuedActionContext(
        Func<bool> StillCurrent,
        ReplayCommandLease? ReplayLease);

    private sealed class QueuedActionScope(
        NativeReplayCameraSession owner,
        QueuedActionContext? previous,
        QueuedActionContext current) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (ReferenceEquals(owner._queuedActionContext.Value, current))
                owner._queuedActionContext.Value = previous;
        }
    }
}

internal readonly record struct ReplaySeekLease(
    long ConnectionGeneration,
    long ReplaySessionGeneration,
    string ReplayName);

internal sealed class ReplaySeekLandingAwaiter
{
    internal const string ReplaySessionChangedMessage =
        "Replay session changed while waiting for seek landing.";

    private readonly int _requestedTick;
    private readonly ReplaySeekLease _lease;
    private readonly TaskCompletionSource<int> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ReplaySeekLandingAwaiter(int requestedTick, ReplaySeekLease lease)
    {
        _requestedTick = requestedTick;
        _lease = lease;
    }

    public Task<int> Completion => _completion.Task;

    public bool ObserveAuthoritativeState(
        ReplayState state,
        long currentConnectionGeneration,
        ReplayState currentState)
    {
        if (!EnsureLeaseCurrent(currentConnectionGeneration, currentState))
            return false;

        // StateChanged notifications are intentionally raised after the
        // controller releases its telemetry lock. If a newer state won that
        // race, the older event object is evidence only and cannot land this
        // seek even when it contains the requested tick.
        if (!ReferenceEquals(state, currentState))
            return false;

        if (!NativeReplayCameraSession.TryGetLandedReplayTick(state, _requestedTick, out var landedTick))
            return false;
        return _completion.TrySetResult(landedTick);
    }

    public bool ObserveSeekCompleted(
        int completedTick,
        long currentConnectionGeneration,
        ReplayState currentState)
    {
        if (!EnsureLeaseCurrent(currentConnectionGeneration, currentState))
            return false;
        if (!NativeReplayCameraSession.IsTickWithinTolerance(completedTick, _requestedTick))
            return false;
        return _completion.TrySetResult(completedTick);
    }

    public bool Expire() => _completion.TrySetException(
        new InvalidOperationException(ReplaySessionChangedMessage));

    private bool EnsureLeaseCurrent(long currentConnectionGeneration, ReplayState currentState)
    {
        if (NativeReplayCameraSession.IsReplaySeekLeaseCurrent(
                _lease,
                currentConnectionGeneration,
                currentState))
            return true;

        Expire();
        return false;
    }
}
