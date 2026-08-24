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
    private const int SeekTickTolerance = 2;
    private const int ObservationTickTolerance = 8;
    private readonly ReplayController _controller;
    private readonly ICameraService _camera;
    private readonly ILogService _log;
    private readonly string _dllPath;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Task _monitorTask;
    private readonly CampathPlaybackStateMachine _playback = new();
    private NativeReplayCameraClient? _client;
    private InProcessCameraStatus? _status;
    private string _message = "Native backend unavailable until replay playback is confirmed.";
    private bool _campathPlaying;
    private bool _holdingKeyframe;
    private bool _manualCameraDesired;
    private int _ownershipCleanupQueued;
    private int _cameraTransferDepth;
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
    public event EventHandler<SmvmAction>? SmvmActionReceived;
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
    public bool Available => Connected && _status?.Flags.HasFlag(InProcessStatusFlags.Resolved) == true;
    public bool CampathPlaying => _campathPlaying;
    public bool ManualCameraDesired => Volatile.Read(ref _manualCameraDesired);
    public bool ManualCameraActive => ManualCameraDesired && _status?.ManualCameraActive == true;
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
    /// Requests native manual-camera ownership without issuing any engine command.
    /// The caller must already have a calibrated replay in Free Roam. Completion
    /// proves that a game-thread hook frame applied the manual camera.
    /// </summary>
    public async Task<InProcessCameraStatus> EnableManualCameraAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _client;
            if (client?.Connected != true)
                throw new InvalidOperationException(_message);
            if (!IsConfirmedReplay(_controller.State) || _controller.GameTickOffset is null)
                throw new InvalidOperationException("Replay playback and its tick calibration must be confirmed.");
            if (_camera.Selection.Mode != SpecCameraMode.FreeRoam)
                throw new InvalidOperationException("SMVM Free Camera is available in Free Roam.");
            if (CampathCameraOwned)
                throw new InvalidOperationException("Stop the current Campath or held keyframe before entering SMVM Free Camera.");

            SetManualCameraDesired(true);
            try
            {
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

                var beforeHookCalls = status.HookCalls;
                status = await client.EnableManualCameraAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus(status);
                if (!status.ManualCameraRequested)
                    throw new InvalidOperationException(
                        $"The native backend rejected SMVM Free Camera ownership ({DescribeNativeStatus(status)}).");

                status = await WaitForManualCameraAsync(
                    client,
                    beforeHookCalls,
                    status,
                    cancellationToken).ConfigureAwait(false);
                _message = "SMVM Free Camera active on a validated game-thread frame.";
                _log.Info($"Native camera: {_message}");
                OnStatusChanged();
                return status;
            }
            catch
            {
                SetManualCameraDesired(false);
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
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Relinquishes native manual-camera ownership. Local desire is cleared
    /// before waiting for the operation gate so managed state fails closed.
    /// </summary>
    public async Task DisableManualCameraAsync(CancellationToken cancellationToken = default)
    {
        SetManualCameraDesired(false);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                var transfer = await connectedClient.SetCameraSampleAsync(probe, cancellationToken).ConfigureAwait(false);
                UpdateStatus(transfer);
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
                    var armed = await connectedClient.EnableOverrideAsync(cancellationToken).ConfigureAwait(false);
                    UpdateStatus(armed);
                    PublishSelfTest(kind, SmvmSelfTestStage.VerifyingAuthoritativeFrame,
                        "Verifying the rendered camera probe through authoritative readback.", probe);
                    var verified = await PollSelfTestStatusAsync(
                        connectedClient,
                        status => status.OverrideActive && status.CameraObserved &&
                                  status.HookCalls > beforeHookCalls &&
                                  SmvmSelfTestPolicy.SamplesMatch(probe, status.Camera),
                        armed,
                        cancellationToken).ConfigureAwait(false);
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
                var transfer = await connectedClient.SetCampathAsync(
                        path, CampathEndBehavior.HoldFinalCamera, cancellationToken)
                    .ConfigureAwait(false);
                UpdateStatus(transfer);
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
                    var armed = await connectedClient.EnableOverrideAsync(cancellationToken).ConfigureAwait(false);
                    UpdateStatus(armed);
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
                if (client?.Connected != true)
                {
                    SetManualCameraDesired(false);
                    result = CompleteSelfTestFailure(
                        kind,
                        SmvmSelfTestFailure.RestorationFailed,
                        "Self-test restoration failed closed because the native backend disconnected.");
                }
                else
                {
                    var restorationError = await RestoreAfterSelfTestAsync(client, kind, policy).ConfigureAwait(false);
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
            var client = _client ?? throw new InvalidOperationException(_message);
            if (_campathPlaying)
                throw new InvalidOperationException("Stop Campath playback before adding a keyframe.");
            if (!IsConfirmedReplay(_controller.State) || _controller.GameTickOffset is null)
                throw new InvalidOperationException("Replay playback and its tick calibration must be confirmed.");
            if (_camera.Selection.Mode != SpecCameraMode.FreeRoam)
                throw new InvalidOperationException("Camera capture is available in Free Roam.");

            var before = await SendHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
            var status = await client.PrepareCameraObservationAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            if (!status.Ready)
                throw new InvalidOperationException($"Native camera observation is unavailable: {status.Error}.");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while ((!status.CameraObserved || status.HookCalls <= before.HookCalls) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus(status);
            }
            if (!status.CameraObserved || status.HookCalls <= before.HookCalls)
                throw new InvalidOperationException($"A fresh renderer camera frame was not observed: {status.Error}.");

            return new CampathKeyframe(status.ReplayTick, status.Camera);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Applies a narrowly typed roll-only override on the spectator camera's game-thread
    /// update. Position, pitch, yaw, FOV, replay state, and POV remain owned by Deadlock.
    /// </summary>
    public async Task<InProcessCameraStatus> SetManualRollAsync(
        double roll,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(roll) || roll is < -180.0 or > 180.0)
            throw new ArgumentOutOfRangeException(nameof(roll));

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _client ?? throw new InvalidOperationException(_message);
            if (_campathPlaying || _status?.OverrideActive == true)
                throw new InvalidOperationException("Release Campath camera ownership before changing manual roll.");
            if (!IsConfirmedReplay(_controller.State) || _controller.GameTickOffset is null)
                throw new InvalidOperationException("Replay playback and its tick calibration must be confirmed.");
            if (_camera.Selection.Mode != SpecCameraMode.FreeRoam)
                throw new InvalidOperationException("Rendered roll is available in Free Roam.");

            var before = await SendHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
            var status = await client.PrepareCameraObservationAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            if (!status.Ready)
                throw new InvalidOperationException($"Native roll control is unavailable: {status.Error}.");

            status = await client.SetRollOverrideAsync(roll, cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while ((!status.RollOverrideActive || !status.CameraObserved || status.HookCalls <= before.HookCalls ||
                    Math.Abs(ShortestAngleDelta(status.Camera.Roll, roll)) > 0.05) &&
                   DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus(status);
            }
            if (!status.RollOverrideActive || !status.CameraObserved || status.HookCalls <= before.HookCalls ||
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

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        EnterCameraTransfer();
        var restorePlayingOnFailure = false;
        try
        {
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

            var originalState = _controller.State;
            restorePlayingOnFailure = originalState.IsPaused == false;
            var currentTick = originalState.CurrentTick!.Value;
            int? observationExpectedTick = playMode == CampathPlayMode.FromStart
                ? checked((int)path.Keyframes[0].DemoTick)
                : null;
            if (playMode == CampathPlayMode.FromCurrent &&
                (currentTick < path.Keyframes[0].DemoTick || currentTick > path.Keyframes[^1].DemoTick))
                ThrowStartFailure(CampathStartFailure.CurrentTickOutsidePath,
                    $"Current replay tick {currentTick} is outside the Campath range " +
                    $"{path.Keyframes[0].DemoTick}-{path.Keyframes[^1].DemoTick}.");

            Transition(CampathPlaybackState.PreparingFreeRoam, "Preparing Free Roam for native camera ownership.");
            try
            {
                await _camera.EnterFreeRoamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ThrowStartFailure(CampathStartFailure.NotInFreeRoam,
                    $"Free Roam could not be prepared: {ex.Message}", ex);
            }

            if (playMode == CampathPlayMode.FromStart)
            {
                var startTick = checked((int)path.Keyframes[0].DemoTick);
                if (Math.Abs((long)currentTick - startTick) > 2)
                {
                    Transition(CampathPlaybackState.SeekingToStart,
                        "SKIPPING TO CAMPATH START", startTick, currentTick);
                    if (originalState.IsPaused != true)
                        _controller.Pause();
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
                    Transition(CampathPlaybackState.ReacquiringFreeRoam,
                        "Reacquiring Free Roam after the landed seek.", startTick, landed);
                    try
                    {
                        await _camera.EnterFreeRoamAsync(cancellationToken).ConfigureAwait(false);
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
            gateStatus = await WaitForFreshStrictFreeRoamCameraAsync(
                client!,
                gateStatus,
                observationExpectedTick,
                "A fresh Free Roam camera frame was not observed before Campath transfer.",
                cancellationToken).ConfigureAwait(false);

            Transition(CampathPlaybackState.TransferringPath,
                $"Transferring {path.Keyframes.Count} typed keyframes to the native replay camera.");
            var transferStatus = await client!.SetCampathAsync(path, endBehavior, cancellationToken).ConfigureAwait(false);
            UpdateStatus(transferStatus);
            if (!transferStatus.Flags.HasFlag(InProcessStatusFlags.CampathActive) ||
                !transferStatus.Flags.HasFlag(InProcessStatusFlags.HasSample))
                ThrowFromNative(transferStatus, CampathStartFailure.PathTransferFailed,
                    "The native backend rejected the Campath payload.");

            Transition(CampathPlaybackState.ArmingNativeCamera,
                "Requesting terminal spectator-camera ownership.");
            var status = await client.EnableOverrideAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
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
                cancellationToken).ConfigureAwait(false);

            _campathPlaying = true;
            _holdingKeyframe = false;
            _activePath = path;
            _endBehavior = endBehavior;
            _controller.Play();
            restorePlayingOnFailure = false;
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
            if (restorePlayingOnFailure)
                _controller.Play();
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

    public async Task<InProcessCameraStatus> GoToKeyframeAsync(
        CampathKeyframe keyframe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyframe);
        if (!keyframe.IsValid)
            throw new ArgumentOutOfRangeException(nameof(keyframe));

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        EnterCameraTransfer();
        try
        {
            var client = _client ?? throw new InvalidOperationException(_message);
            if (!IsConfirmedReplay(_controller.State) || _controller.GameTickOffset is null)
                throw new InvalidOperationException("Replay playback and its tick calibration must be confirmed.");

            // A prior Go To intentionally holds a full native override. Release
            // that ownership before seeking so the old shot cannot reappear if
            // Deadlock temporarily leaves and then re-enters Free Roam.
            if (CampathCameraOwned)
                await StopCampathCoreAsync(cancellationToken).ConfigureAwait(false);

            _controller.Pause();
            var landed = await SeekToPathStartAsync(
                checked((int)keyframe.DemoTick),
                cancellationToken).ConfigureAwait(false);
            await _camera.EnterFreeRoamAsync(cancellationToken).ConfigureAwait(false);
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
            var transfer = await client.SetCameraSampleAsync(keyframe.Camera, cancellationToken).ConfigureAwait(false);
            UpdateStatus(transfer);
            var status = await client.EnableOverrideAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            status = await WaitForNativeAsync(
                client,
                candidate => candidate.OverrideActive && candidate.AppliedSequence >= transfer.AcceptedSequence,
                TimeSpan.FromSeconds(2),
                CampathStartFailure.CameraOwnershipRejected,
                "Native camera did not acquire the selected shot.",
                status,
                cancellationToken).ConfigureAwait(false);
            status = await EnsureManualCameraArmedUnderOverrideAsync(
                client,
                status,
                cancellationToken).ConfigureAwait(false);
            _campathPlaying = false;
            _activePath = null;
            _holdingKeyframe = true;
            _message = $"Holding keyframe at tick {keyframe.DemoTick}; Stop releases camera control.";
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

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var preservingManualCamera = false;
        try
        {
            if (!_campathPlaying || _activePath is not { } path)
            {
                _controller.SeekToTick(tick);
                return null;
            }

            EnterCameraTransfer();
            preservingManualCamera = true;

            var client = _client;
            if (client?.Connected != true)
                throw new CampathStartException(CampathStartFailure.NativeBackendDisconnected, _message);

            var wasPaused = _controller.State.IsPaused == true;
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
            try
            {
                await _camera.EnterFreeRoamAsync(cancellationToken).ConfigureAwait(false);
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
            var transferStatus = await client.SetCampathAsync(path, _endBehavior, cancellationToken)
                .ConfigureAwait(false);
            UpdateStatus(transferStatus);
            if (!transferStatus.Flags.HasFlag(InProcessStatusFlags.CampathActive) ||
                !transferStatus.Flags.HasFlag(InProcessStatusFlags.HasSample))
                ThrowFromNative(transferStatus, CampathStartFailure.PathTransferFailed,
                    "The native backend rejected the Campath while recovering from seek.");

            Transition(CampathPlaybackState.ArmingNativeCamera,
                "Rearming terminal spectator-camera ownership after seek.", tick, landed);
            var status = await client.EnableOverrideAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
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
                cancellationToken).ConfigureAwait(false);

            if (wasPaused)
                _controller.Pause();
            else
                _controller.Play();
            Transition(CampathPlaybackState.Playing,
                $"Campath reacquired at landed tick {landed}.", tick, landed);
            return landed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
            if (preservingManualCamera)
                ExitCameraTransfer();
            _operationGate.Release();
        }
    }

    private async Task<InProcessCameraStatus> EnsureManualCameraArmedUnderOverrideAsync(
        NativeReplayCameraClient client,
        InProcessCameraStatus status,
        CancellationToken cancellationToken)
    {
        if (!ShouldArmManualCameraUnderOverride(ManualCameraDesired, status))
            return status;
        if (!CanResumeManualCamera(
                ManualCameraDesired,
                _controller.State,
                _controller.GameTickOffset,
                _camera.Selection.Mode))
        {
            SetManualCameraDesired(false);
            return status;
        }

        await PublishFreshSmvmSnapshotAsync(client, required: false, cancellationToken)
            .ConfigureAwait(false);
        status = await client.EnableManualCameraAsync(cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
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
        using var cleanupTimeout = new CancellationTokenSource(BestEffortCleanupTimeout);
        var cleanupToken = cleanupTimeout.Token;
        var resumeManualCamera = CanResumeManualCamera(
            ManualCameraDesired,
            _controller.State,
            _controller.GameTickOffset,
            _camera.Selection.Mode);
        var releaseManualCamera = ShouldReleaseManualCameraOnStop(
            resumeManualCamera,
            ManualCameraDesired,
            _status?.Flags ?? InProcessStatusFlags.None);
        if (ManualCameraDesired && !resumeManualCamera)
            SetManualCameraDesired(false);

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
                    cleanupToken).ConfigureAwait(false);
                manualResumeArmed = status.ManualCameraRequested;
            }
            catch (Exception ex)
            {
                _log.Warn($"Native camera: {context} could not preserve manual camera: {ex.Message}");
            }
        }

        var beforeReleaseHookCalls = status?.HookCalls ?? 0;
        try
        {
            status = await client.DisableOverrideAsync(cleanupToken).ConfigureAwait(false);
            UpdateStatus(status);
            status = await client.ClearCampathAsync(cleanupToken).ConfigureAwait(false);
            UpdateStatus(status);
            if (resumeManualCamera && manualResumeArmed)
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
    }

    internal static bool IsFreshManualCameraFrame(
        InProcessCameraStatus status,
        ulong beforeHookCalls) =>
        status.ManualCameraRequested &&
        status.ManualCameraActive &&
        status.CameraObserved &&
        status.HookCalls > beforeHookCalls;

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
        if (_playback.Status.State != CampathPlaybackState.Stopping)
            Transition(CampathPlaybackState.Stopping, "Releasing native Campath camera ownership.");
        var client = _client;
        var resumeManualCamera = CanResumeManualCamera(
            ManualCameraDesired,
            _controller.State,
            _controller.GameTickOffset,
            _camera.Selection.Mode);
        var releaseManualCamera = ShouldReleaseManualCameraOnStop(
            resumeManualCamera,
            ManualCameraDesired,
            _status?.Flags ?? InProcessStatusFlags.None);
        if (ManualCameraDesired && !resumeManualCamera)
            SetManualCameraDesired(false);

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
                        cancellationToken).ConfigureAwait(false);
                    manualResumeArmed = status.ManualCameraRequested;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn($"Native camera: manual camera could not be armed before Campath release: {ex.Message}");
                }
            }

            var beforeReleaseHookCalls = status?.HookCalls ?? 0;
            try
            {
                status = await client.DisableOverrideAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus(status);
                status = await client.ClearCampathAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus(status);
                if (resumeManualCamera && manualResumeArmed)
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
        }

        _message = resumeManualCamera
            ? manualResumeConfirmed
                ? "Campath stopped; SMVM Free Camera resumed from the final shot."
                : "Campath stopped; Free Camera remains requested but its resumed frame is not yet confirmed."
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
                if (_client?.Connected != true)
                    await TryConnectAsync(token).ConfigureAwait(false);
                else
                {
                    await SendHeartbeatAsync(_client, token).ConfigureAwait(false);
                    if (SmvmSnapshotProvider?.Invoke() is { } snapshot)
                        UpdateStatus(await _client.UpdateSmvmSnapshotAsync(snapshot, token)
                            .ConfigureAwait(false));
                }
            },
            HandleMonitorFailureAsync,
            token => timer.WaitForNextTickAsync(token),
            cancellationToken).ConfigureAwait(false);
    }

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
        SetManualCameraDesired(false);
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

        var client = _client;
        _client = null;
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

        _message = $"Native backend failed: {ex.Message} Retrying while replay playback remains available.";
        _log.Warn(_message);
        OnStatusChanged();
    }

    private async Task TryConnectAsync(CancellationToken cancellationToken)
    {
        var replay = _controller.State;
        if (!IsConfirmedReplay(replay) || _controller.GameTickOffset is null)
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
        try
        {
            var status = await client.ConnectAsync(process.Id, TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            _client = client;
            UpdateStatus(status);
            _message = status.Error == InProcessErrorCode.None
                ? "Native backend connected; camera override is armed only in Free Roam."
                : $"Native backend connected with error: {status.Error}.";
            _log.Info($"Native camera: {load.Message} Protocol v{InProcessProtocol.Version} connected.");
            await SendHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<InProcessCameraStatus> SendHeartbeatAsync(
        NativeReplayCameraClient client,
        CancellationToken cancellationToken)
    {
        var replay = _controller.State;
        var replayActive = IsConfirmedReplay(replay);
        var freeRoam = _camera.Selection.Mode == SpecCameraMode.FreeRoam;
        var hadManualCamera = ManualCameraDesired ||
                              _status?.ManualCameraRequested == true ||
                              _status?.ManualCameraActive == true;
        var status = await client.HeartbeatAsync(
            replayActive,
            freeRoam,
            replay.CurrentTick ?? -1,
            _controller.GameTickOffset ?? -1,
            cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        if (_campathPlaying && _activePath is { } activePath &&
            _playback.Status.State == CampathPlaybackState.Playing)
        {
            if (_endBehavior == CampathEndBehavior.StopAndRelease &&
                status.ReplayTick >= activePath.Keyframes[^1].DemoTick)
            {
                _campathPlaying = false;
                Transition(CampathPlaybackState.Completed,
                    $"Campath reached final tick {activePath.Keyframes[^1].DemoTick}.",
                    activePath.Keyframes[^1].DemoTick,
                    status.ReplayTick);
                _ = CompleteAndReleaseAsync();
            }
            else if (!status.OverrideActive &&
                     _playback.Status.State == CampathPlaybackState.Playing &&
                     _camera.Selection.Mode == SpecCameraMode.FreeRoam)
            {
                var failure = MapNativeFailure(status, CampathStartFailure.CameraOwnershipRejected);
                _playback.Fail(failure,
                    $"Campath lost native camera ownership ({DescribeNativeStatus(status)})." );
                RaiseCampathState();
                _ = StopAfterOwnershipLossAsync(_playback.Status.Detail);
            }
        }
        if (_holdingKeyframe && replayActive && !status.OverrideActive &&
            _camera.Selection.Mode == SpecCameraMode.FreeRoam &&
            Interlocked.Exchange(ref _ownershipCleanupQueued, 1) == 0)
        {
            _ = StopAfterOwnershipLossAsync("Held keyframe lost native camera ownership.");
        }
        if (!replayActive && (hadManualCamera || _campathPlaying || _holdingKeyframe))
        {
            SetManualCameraDesired(false);
            _campathPlaying = false;
            _holdingKeyframe = false;
            _activePath = null;
            _message = "Native camera ownership released because replay playback ended.";
            OnStatusChanged();
        }
        else if (!freeRoam && Volatile.Read(ref _cameraTransferDepth) == 0)
        {
            OnSelectionChanged(this, EventArgs.Empty);
        }
        return status;
    }

    private async Task<int> SeekToPathStartAsync(int tick, CancellationToken cancellationToken)
    {
        if (TryGetLandedReplayTick(_controller.State, tick, out var landed))
            return landed;

        var landing = new ReplaySeekLandingAwaiter(tick);
        void OnCompleted(object? sender, int completedTick)
        {
            landing.ObserveSeekCompleted(completedTick);
        }

        void OnStateChanged(object? sender, ReplayState state)
        {
            landing.ObserveAuthoritativeState(state);
        }

        _controller.SeekCompleted += OnCompleted;
        _controller.StateChanged += OnStateChanged;
        try
        {
            // Close the small read/subscribe race before issuing a command. A
            // current-tick Go To is already complete and Deadlock may emit no
            // seek-finished console line for that no-op.
            if (TryGetLandedReplayTick(_controller.State, tick, out landed))
                return landed;

            _controller.SeekToTick(tick);
            OnStateChanged(_controller, _controller.State);
            return await landing.Completion.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
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

        var releaseManualCamera = ManualCameraDesired ||
                                  flags.HasFlag(InProcessStatusFlags.ManualCameraRequested) ||
                                  flags.HasFlag(InProcessStatusFlags.ManualCameraActive);
        var releaseFullOverride = HasFullCameraOwnershipIntent(_campathPlaying, _holdingKeyframe, flags);
        SetManualCameraDesired(false);
        if (Interlocked.Exchange(ref _ownershipCleanupQueued, 1) == 0)
        {
            _ = StopAfterPovLossAsync(
                "Native camera ownership released because POV mode changed.",
                releaseManualCamera,
                releaseFullOverride);
        }
    }

    private async Task StopAfterPovLossAsync(
        string message,
        bool releaseManualCamera,
        bool releaseFullOverride)
    {
        try
        {
            await _operationGate.WaitAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                var client = _client;
                if (releaseManualCamera && client?.Connected == true)
                    await TryDisableManualCameraAsync(client, _stop.Token).ConfigureAwait(false);
                if (releaseFullOverride)
                    await StopCampathCoreAsync(_stop.Token).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }

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

    private async Task StopAfterOwnershipLossAsync(string message)
    {
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
        HasCameraOwnershipIntent(campathPlaying, holdingKeyframe, manualCameraDesired, flags);

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

    internal static bool ShouldFailClosedManualCameraAfterNativeLoss(
        bool manualCameraDesired,
        InProcessCameraStatus? previous,
        InProcessCameraStatus current,
        bool cameraTransferInProgress)
    {
        if (!manualCameraDesired || cameraTransferInProgress || previous?.ManualCameraActive != true ||
            current.ManualCameraRequested || current.ManualCameraActive)
            return false;
        return true;
    }

    private void SetManualCameraDesired(bool desired)
    {
        if (ManualCameraDesired == desired)
            return;
        Volatile.Write(ref _manualCameraDesired, desired);
        OnStatusChanged();
    }

    private void EnterCameraTransfer() => Interlocked.Increment(ref _cameraTransferDepth);

    private void ExitCameraTransfer()
    {
        if (Interlocked.Decrement(ref _cameraTransferDepth) == 0 &&
            _camera.Selection.Mode != SpecCameraMode.FreeRoam)
            OnSelectionChanged(this, EventArgs.Empty);
    }

    private async Task CompleteAndReleaseAsync()
    {
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
        SmvmSelfTestRestorationPolicy restoration)
    {
        using var restorationTimeout = new CancellationTokenSource(SelfTestRestorationTimeout);
        var restorationToken = restorationTimeout.Token;
        try
        {
            PublishSelfTest(kind, SmvmSelfTestStage.RestoringCamera,
                "Restoring the exact baseline camera sample.", restoration.Baseline);
            var transfer = await client.SetCameraSampleAsync(restoration.Baseline, restorationToken)
                .ConfigureAwait(false);
            UpdateStatus(transfer);
            var beforeHookCalls = transfer.HookCalls;
            var armed = await client.EnableOverrideAsync(restorationToken).ConfigureAwait(false);
            UpdateStatus(armed);
            var restored = await PollSelfTestStatusAsync(
                client,
                status => status.OverrideActive && status.CameraObserved && status.HookCalls > beforeHookCalls &&
                          SmvmSelfTestPolicy.SamplesMatch(restoration.Baseline, status.Camera),
                armed,
                restorationToken).ConfigureAwait(false);
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
                SetManualCameraDesired(true);
                await PublishFreshSmvmSnapshotAsync(client, required: true, restorationToken)
                    .ConfigureAwait(false);
                var prepared = await client.PrepareCameraObservationAsync(restorationToken).ConfigureAwait(false);
                UpdateStatus(prepared);
                var requested = await client.EnableManualCameraAsync(restorationToken).ConfigureAwait(false);
                UpdateStatus(requested);
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
                var rollStatus = await client.SetRollOverrideAsync(
                    restoration.Baseline.Roll, restorationToken).ConfigureAwait(false);
                UpdateStatus(rollStatus);
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
        SelfTestStateChanged?.Invoke(this, result);
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

    private void RaiseCampathState() => CampathStateChanged?.Invoke(this, _playback.Status);

    private void UpdateStatus(InProcessCameraStatus status)
    {
        var previous = _status;
        var manualCameraLost = ShouldFailClosedManualCameraAfterNativeLoss(
            ManualCameraDesired,
            previous,
            status,
            Volatile.Read(ref _cameraTransferDepth) > 0);
        _status = status;
        if (manualCameraLost)
        {
            Volatile.Write(ref _manualCameraDesired, false);
            _message = $"SMVM Free Camera ownership was revoked by the native backend " +
                       $"({DescribeNativeStatus(status)}). Reacquire explicitly after the camera gate is healthy.";
            _log.Warn($"Native camera: {_message}");
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
        if (status.Action.Type != SmvmActionType.None)
            SmvmActionReceived?.Invoke(this, status.Action);
        OnStatusChanged();
    }

    public async Task PublishEditorCampathAsync(
        IReadOnlyList<CampathKeyframe> keyframes,
        CampathInterpolationMode interpolation,
        CampathEasingMode easing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        var client = _client;
        if (client?.Connected != true)
            return;
        InProcessCameraStatus status;
        if (keyframes.Count == 0)
        {
            status = await client.ClearEditorCampathAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            status = await client.SetEditorCampathAsync(keyframes, interpolation, easing, cancellationToken)
                .ConfigureAwait(false);
        }
        UpdateStatus(status);
    }

    /// <summary>Publishes the bounded saved-path picker list for the internal Load UI.</summary>
    public async Task PublishCampathDocumentsAsync(
        IReadOnlyList<CampathDocumentInfo> documents,
        CampathReplayIdentifier? currentReplay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var client = _client;
        if (client?.Connected != true)
            return;
        var status = await client.SetCampathDocumentsAsync(
            documents.Count > InProcessProtocol.MaxCampathDocuments
                ? documents.Take(InProcessProtocol.MaxCampathDocuments).ToArray()
                : documents,
            currentReplay,
            cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
    }

    private void OnStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private static bool IsConfirmedReplay(ReplayState replay) =>
        replay.Connected && !string.IsNullOrWhiteSpace(replay.ReplayName) && replay.CurrentTick is not null &&
        (replay.TotalTicks is null || replay.CurrentTick < replay.TotalTicks);

    private static Process? FindDeadlockProcess() =>
        Process.GetProcessesByName("deadlock").FirstOrDefault() ??
        Process.GetProcessesByName("project8").FirstOrDefault();

    public async ValueTask DisposeAsync()
    {
        _camera.SelectionChanged -= OnSelectionChanged;
        SetManualCameraDesired(false);
        SmvmSnapshotProvider = null;
        _stop.Cancel();
        try { await _monitorTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var client = _client;
            _client = null;
            if (client is not null)
            {
                try
                {
                    if (client.Connected)
                    {
                        UpdateStatus(await client.DisableManualCameraAsync().ConfigureAwait(false));
                        UpdateStatus(await client.DisableOverrideAsync().ConfigureAwait(false));
                        UpdateStatus(await client.ShutdownAsync().ConfigureAwait(false));
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    _log.Warn($"Native camera: shutdown response unavailable: {ex.Message}");
                }
                await client.DisposeAsync().ConfigureAwait(false);
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
            _operationGate.Release();
            _operationGate.Dispose();
            _stop.Dispose();
        }
    }

    private static double ShortestAngleDelta(double from, double to)
    {
        var delta = (to - from) % 360.0;
        if (delta > 180.0) delta -= 360.0;
        if (delta < -180.0) delta += 360.0;
        return delta;
    }
}

internal sealed class ReplaySeekLandingAwaiter
{
    private readonly int _requestedTick;
    private readonly TaskCompletionSource<int> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ReplaySeekLandingAwaiter(int requestedTick)
    {
        _requestedTick = requestedTick;
    }

    public Task<int> Completion => _completion.Task;

    public bool ObserveAuthoritativeState(ReplayState state)
    {
        if (!NativeReplayCameraSession.TryGetLandedReplayTick(state, _requestedTick, out var landedTick))
            return false;
        return _completion.TrySetResult(landedTick);
    }

    public bool ObserveSeekCompleted(int completedTick)
    {
        if (!NativeReplayCameraSession.IsTickWithinTolerance(completedTick, _requestedTick))
            return false;
        return _completion.TrySetResult(completedTick);
    }
}
