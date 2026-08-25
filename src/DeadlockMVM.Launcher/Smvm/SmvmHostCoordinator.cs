using System.Windows.Threading;
using System.IO;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Native.InProcess;
using DeadlockMVM.Core.Services;

namespace DeadlockMVM.Launcher.Smvm;

/// <summary>
/// Bridges the in-process SMVM renderer to the existing managed sources of
/// truth. The native render/game threads only exchange immutable snapshots and
/// typed actions; every VConsole, camera, persistence, and editor operation is
/// performed here on the managed/UI side.
/// </summary>
public sealed class SmvmHostCoordinator : IAsyncDisposable
{
    private readonly record struct ForwardPresentationLease(
        long ConnectionEpoch,
        long IntentEpoch,
        long ReplayEpoch);

    private sealed record EditorSnapshot(
        string Name,
        string Status,
        CampathKeyframe[] Keyframes,
        int SelectedIndex,
        CampathInterpolationMode Interpolation,
        CampathEasingMode Easing,
        CampathEndBehavior EndBehavior,
        CampathSessionState Session,
        bool RecoveryAvailable,
        int SavedDocumentCount);

    private readonly ICameraService _camera;
    private readonly ReplayController _controller;
    private readonly NativeReplayCameraSession _native;
    private readonly CampathViewModel _campath;
    private readonly IAppSettings _settings;
    private readonly ILogService _log;
    private readonly DeadlockUiController _deadlockUi;
    private readonly DemoStartupPolicy _demoStartup = new();
    private readonly DemoPlaybackSpeedPolicy _demoPlaybackSpeed = new();
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly SemaphoreSlim _pathPublishGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _editorGate = new();
    private readonly object _presentationReplayGate = new();
    private readonly object _processBoundaryGate = new();
    private readonly bool _captureDiagnosticsEnabled;
    private EditorSnapshot _editor;
    private int _editorRevision;
    private int _publishedRevision = -1;
    private int _pathPublishPending;
    private int _replaySeekInFlight;
    private long _presentationIntentEpoch;
    private int _stopping;
    private long _presentationReplayEpoch;
    private string _presentationReplayIdentity = string.Empty;
    private int _nativeWasConnected;
    private int _nativeProcessId;
    private long _processBoundaryEpoch;
    private long _minimumProcessTelemetryGeneration;
    private int _processProfileReassertPending;
    private string _observedReplayIdentity = string.Empty;
    private bool? _observedReplayPaused;

    public SmvmHostCoordinator(
        ICameraService camera,
        ReplayController controller,
        NativeReplayCameraSession native,
        CampathViewModel campath,
        IAppSettings settings,
        ILogService log,
        Dispatcher dispatcher)
    {
        _camera = camera;
        _controller = controller;
        _native = native;
        _campath = campath;
        _settings = settings;
        _log = log;
        _deadlockUi = new DeadlockUiController(controller, log);
        _dispatcher = dispatcher;
        var captureTrace = Environment.GetEnvironmentVariable("DEADLOCKMVM_CAPTURE_TRACE");
        _captureDiagnosticsEnabled = string.Equals(captureTrace, "1", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(captureTrace, "true", StringComparison.OrdinalIgnoreCase);
        _editor = ReadEditorSnapshot();
        if (UpdatePresentationReplayEpoch(_controller.State))
            _deadlockUi.NormalizeTransientCleanForNewReplay();
        _native.SmvmSnapshotProvider = CreateSnapshot;
        _native.SmvmActionReceived += OnActionReceived;
        _native.StatusChanged += OnNativeStatusChanged;
        _campath.EditorStateChanged += OnEditorStateChanged;
        _native.CampathStateChanged += OnCampathStateChanged;
        _controller.StateChanged += OnReplayStateChanged;
        _ = PublishEditorPathAsync();
        _ = CoordinateDemoStartupAsync(_controller.State);
    }

    private void OnActionReceived(object? sender, SmvmActionDispatch dispatch)
    {
        if (Volatile.Read(ref _stopping) != 0)
            return;
        var action = dispatch.Action;
        var connectionEpoch = dispatch.ConnectionEpoch;
        string? playbackSpeedReplayIdentity = null;
        long playbackSpeedOwnerIntentEpoch = 0;
        long processBoundaryEpoch;
        (long ConnectionGeneration, ReplayState State) replaySnapshot;
        lock (_processBoundaryGate)
        {
            // Native callbacks can unwind after their client was retired. Do
            // not let stale ingress mutate startup policy or perform the
            // immediate Escape/F9 inverse against the replacement connection.
            if (connectionEpoch <= 0 ||
                !_native.IsConnectionLeaseCurrent(connectionEpoch))
            {
                return;
            }
            replaySnapshot = _controller.CaptureReplayTelemetrySnapshot();
            var actionScope = SmvmQueuedActionLeasePolicy.GetScope(action);
            var authoritativeReplay = HasAuthoritativeReplayTelemetry(replaySnapshot.State);
            var sourceMatchesProvisionalReplay = replaySnapshot.State.ReplaySessionGeneration > 0
                ? dispatch.ReplaySessionGeneration == replaySnapshot.State.ReplaySessionGeneration
                : dispatch.ReplaySessionGeneration == 0;
            var rejectSource = actionScope switch
            {
                SmvmQueuedActionScope.ReplayIdentity =>
                    !authoritativeReplay ||
                    dispatch.ReplaySessionGeneration <= 0 ||
                    dispatch.ReplaySessionGeneration != replaySnapshot.State.ReplaySessionGeneration,
                SmvmQueuedActionScope.PresentationReplay or SmvmQueuedActionScope.PlaybackSpeed =>
                    authoritativeReplay
                        ? dispatch.ReplaySessionGeneration <= 0 ||
                          dispatch.ReplaySessionGeneration != replaySnapshot.State.ReplaySessionGeneration
                        : !sourceMatchesProvisionalReplay,
                _ => false,
            };
            if (rejectSource)
            {
                if (action.Type == SmvmActionType.SetTimescale)
                    _log.Info("Ignored replay-speed input from a superseded replay snapshot.");
                return;
            }
            if (action.Type == SmvmActionType.SetTimescale)
            {
                var speedReplay = replaySnapshot.State;
                playbackSpeedReplayIdentity = IsReplayActive(speedReplay)
                    ? DemoStartupPolicy.CreateReplayIdentity(
                        speedReplay.ReplayName!,
                        speedReplay.TotalTicks,
                        speedReplay.ReplaySessionGeneration)
                    : null;
                playbackSpeedOwnerIntentEpoch =
                    _demoPlaybackSpeed.MarkOwnerIntent(playbackSpeedReplayIdentity);
            }
            if (IsDemoStartupOwnerOverride(action))
            {
                var replay = replaySnapshot.State;
                var authoritative = HasAuthoritativeReplayTelemetry(replay);
                if (!authoritative)
                {
                    _demoStartup.MarkOwnerOverride(scopeToNextObservedSession: true);
                }
                else if (IsReplayActive(replay))
                {
                    _demoStartup.MarkOwnerOverride(
                        DemoStartupPolicy.CreateReplayIdentity(
                            replay.ReplayName!,
                            replay.TotalTicks,
                            replay.ReplaySessionGeneration));
                }
                Interlocked.Increment(ref _presentationIntentEpoch);
            }
            processBoundaryEpoch = Volatile.Read(ref _processBoundaryEpoch);
            // Exit/F9 are immediate owner inverses and must not wait behind a
            // seek or self-test. The process-boundary lock makes validation and
            // these synchronous effects one host-side linearization point.
            if (IsExplicitFreeCameraExitAction(action.Type))
                _native.CancelManualCameraIntent();
            if (action.Type == SmvmActionType.RestoreDeadlockUi)
            {
                _deadlockUi.Restore(force: true, RecoveryGeneration(action));
                _settings.SmvmDeadlockUiMode = DeadlockUiMode.DeadlockUi;
                _settings.Save();
            }
        }
        var presentationIntentEpoch = Volatile.Read(ref _presentationIntentEpoch);
        var presentationReplayEpoch = CurrentPresentationReplayEpoch;
        var playbackSpeedActionLease = new DemoPlaybackSpeedActionLease(
            playbackSpeedOwnerIntentEpoch,
            processBoundaryEpoch,
            connectionEpoch,
            presentationReplayEpoch,
            playbackSpeedReplayIdentity);
        var queuedActionLease = new SmvmQueuedActionLease(
            processBoundaryEpoch,
            connectionEpoch,
            presentationReplayEpoch,
            replaySnapshot.ConnectionGeneration,
            replaySnapshot.State.ReplaySessionGeneration,
            replaySnapshot.State.ReplayName?.Trim() ?? string.Empty,
            dispatch.ReplaySessionGeneration);
        BeginInvokeIsolated(
            () => _ = ExecuteActionSafeAsync(
                action,
                queuedActionLease,
                presentationIntentEpoch,
                playbackSpeedActionLease));
    }

    internal static bool IsExplicitFreeCameraExitAction(SmvmActionType action) =>
        action is SmvmActionType.ToggleManualCamera or SmvmActionType.RestoreDeadlockUi or
            SmvmActionType.PreviousPlayer or SmvmActionType.NextPlayer or
            SmvmActionType.InEye or SmvmActionType.Chase;

    private void BeginInvokeIsolated(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (ExecutionContext.IsFlowSuppressed())
        {
            _dispatcher.BeginInvoke(callback);
            return;
        }
        using (ExecutionContext.SuppressFlow())
            _dispatcher.BeginInvoke(callback);
    }

    private bool IsDemoStartupOwnerOverride(SmvmAction action) =>
        IsExplicitFreeCameraExitAction(action.Type) ||
        action.Type is SmvmActionType.CycleReplayInterface ||
        (action.Type == SmvmActionType.ToggleReplayPause &&
         action.Index <= 0) ||
        (action.Type == SmvmActionType.SetDeadlockUiMode &&
         action.Tick < 0);

    private void OnEditorStateChanged(object? sender, EventArgs e)
    {
        RefreshEditorSnapshot();
        _ = PublishEditorPathAsync();
    }

    private void OnNativeStatusChanged(object? sender, EventArgs e)
    {
        var connected = _native.Connected;
        var connectionBecameLive = connected &&
            Interlocked.Exchange(ref _nativeWasConnected, 1) == 0;
        var processId = _native.Status?.ProcessId ?? 0;
        var previousProcessId = processId > 0
            ? Interlocked.Exchange(ref _nativeProcessId, processId)
            : Volatile.Read(ref _nativeProcessId);
        var processChanged = previousProcessId > 0 && processId > 0 &&
                             previousProcessId != processId;
        if (processChanged)
        {
            lock (_processBoundaryGate)
            {
                // Quarantine every startup/profile/speed action before invalidating
                // VConsole state. Native publishes the candidate PID while loading,
                // so this also catches A -> B when A never completed its pipe lease.
                Interlocked.Increment(ref _processBoundaryEpoch);
                Volatile.Write(ref _minimumProcessTelemetryGeneration, long.MaxValue);
                Interlocked.Exchange(ref _processProfileReassertPending, 1);
                var requiredGeneration = _controller.BeginProcessBoundaryTelemetryFence();
                _demoPlaybackSpeed.ResetForNewProcess();
                _demoStartup.ResetForNewProcess();
                _deadlockUi.NormalizeTransientCleanForNewReplay();
                Volatile.Write(ref _minimumProcessTelemetryGeneration, requiredGeneration);
            }
        }
        if (connectionBecameLive)
        {
            _ = PublishEditorPathAsync();
        }
        else if (!connected)
        {
            Interlocked.Exchange(ref _nativeWasConnected, 0);
        }
        BeginInvokeIsolated(
            () => _ = CoordinateAfterNativeStatusAsync(connectionBecameLive));
    }

    private async Task CoordinateAfterNativeStatusAsync(bool connectionBecameLive)
    {
        var processBoundaryEpoch = Volatile.Read(ref _processBoundaryEpoch);
        if (connectionBecameLive &&
            Volatile.Read(ref _processProfileReassertPending) == 0)
        {
            // TryConnect publishes only after native acknowledged the first
            // snapshot. Reassert the managed logical profile once for this new
            // physical module/pipe lease; fresh Deadlock defaults cannot inherit
            // an acknowledgement from an older process.
            await ReassertMovieUiWithConnectionLeaseAsync(
                    CaptureForwardPresentationLease(),
                    processBoundaryEpoch)
                .ConfigureAwait(true);
        }
        if (!IsProcessBoundaryCurrent(processBoundaryEpoch))
            return;
        await CoordinateDemoStartupAsync(_controller.State).ConfigureAwait(true);
    }

    private void OnCampathStateChanged(object? sender, CampathPlaybackStatus status) =>
        BeginInvokeIsolated(RefreshEditorSnapshot);

    private void OnReplayStateChanged(object? sender, ReplayState state)
    {
        // This callback can run synchronously inside a failed VConsole profile
        // write while DeadlockUiController owns its transaction lock. Never
        // acquire the process-boundary lock here (profile -> process would
        // invert the process -> profile order used by PID replacement).
        if (!ReferenceEquals(state, _controller.State))
            return;
        var processBoundaryEpoch = Volatile.Read(ref _processBoundaryEpoch);
        var previousPresentationReplayEpoch = CurrentPresentationReplayEpoch;
        var normalizeTransientClean = UpdatePresentationReplayEpoch(state);
        var presentationReplayEpoch = CurrentPresentationReplayEpoch;
        var replayEpochChanged =
            presentationReplayEpoch != previousPresentationReplayEpoch;
        BeginInvokeIsolated(() =>
        {
            var current = _controller.State;
            if (normalizeTransientClean &&
                IsProcessBoundaryCurrent(processBoundaryEpoch) &&
                CurrentPresentationReplayEpoch == presentationReplayEpoch &&
                HasAuthoritativeReplayTelemetry(current))
            {
                _deadlockUi.NormalizeTransientCleanForNewReplay();
            }
            if (replayEpochChanged)
                _publishedRevision = -1;
            RefreshEditorSnapshot();
            if (replayEpochChanged)
                _ = PublishEditorPathAsync();
            ReassertMovieUiAfterTransportBoundary(current);
            _ = CoordinateDemoStartupAsync(current);
        });
    }

    private void ReassertMovieUiAfterTransportBoundary(ReplayState replay)
    {
        var processBoundaryEpoch = Volatile.Read(ref _processBoundaryEpoch);
        lock (_processBoundaryGate)
        {
            if (!IsProcessBoundaryCurrent(processBoundaryEpoch) ||
                !HasAuthoritativeReplayTelemetry(replay))
            {
                return;
            }
            if (!IsReplayActive(replay))
            {
                _observedReplayIdentity = string.Empty;
                _observedReplayPaused = null;
                return;
            }

            var identity = DemoStartupPolicy.CreateReplayIdentity(
                replay.ReplayName!,
                replay.TotalTicks,
                replay.ReplaySessionGeneration);
            if (!string.Equals(identity, _observedReplayIdentity, StringComparison.Ordinal))
            {
                var replacedActiveReplay = !string.IsNullOrWhiteSpace(_observedReplayIdentity);
                _observedReplayIdentity = identity;
                _observedReplayPaused = replay.IsPaused;
                if (replacedActiveReplay)
                {
                    // Direct demo-to-demo loads can recreate Panorama and visual
                    // cvars without an inactive/end observation. Reassert once
                    // through the new replay epoch; Deadlock mode remains a no-op.
                    _ = ReassertMovieUiWithConnectionLeaseAsync(
                        CaptureForwardPresentationLease(),
                        processBoundaryEpoch);
                }
                return;
            }

            var pauseChanged = replay.IsPaused is not null && _observedReplayPaused is not null &&
                               replay.IsPaused != _observedReplayPaused;
            if (replay.IsPaused is not null)
                _observedReplayPaused = replay.IsPaused;
            if (pauseChanged)
                _ = ReassertMovieUiWithConnectionLeaseAsync(
                    CaptureForwardPresentationLease(),
                    processBoundaryEpoch);
        }
    }

    private async Task CoordinateDemoStartupAsync(ReplayState replay)
    {
        var processBoundaryEpoch = Volatile.Read(ref _processBoundaryEpoch);
        var playbackSpeedLease = _demoPlaybackSpeed.CaptureLease();
        replay = _controller.State;
        var requiredTelemetryGeneration =
            Volatile.Read(ref _minimumProcessTelemetryGeneration);
        if (requiredTelemetryGeneration > 0)
        {
            if (requiredTelemetryGeneration == long.MaxValue ||
                _controller.AuthoritativeTelemetryGeneration < requiredTelemetryGeneration ||
                !HasAuthoritativeReplayTelemetry(replay))
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref _minimumProcessTelemetryGeneration,
                    0,
                    requiredTelemetryGeneration) != requiredTelemetryGeneration &&
                Volatile.Read(ref _minimumProcessTelemetryGeneration) > 0)
            {
                return;
            }
            replay = _controller.State;
        }
        if (!IsProcessBoundaryCurrent(processBoundaryEpoch))
            return;

        // A replacement Deadlock process starts with fresh HUD/cvar state.
        // Reassert only after both its marker-fenced telemetry and native pipe
        // are authoritative; retain the debt if either side arrives first.
        if (_native.Connected &&
            Interlocked.CompareExchange(ref _processProfileReassertPending, 0, 1) == 1)
        {
            var reasserted = await ReassertMovieUiWithConnectionLeaseAsync(
                    CaptureForwardPresentationLease(),
                    processBoundaryEpoch)
                .ConfigureAwait(true);
            if (!reasserted)
                Interlocked.CompareExchange(ref _processProfileReassertPending, 1, 0);
            if (!IsProcessBoundaryCurrent(processBoundaryEpoch))
                return;
            replay = _controller.State;
        }

        var replayActive = IsReplayActive(replay);
        if (!replay.Connected)
        {
            // ReplayState.Empty is also the transient VConsole-disconnect
            // sentinel. It must not reset same-demo startup completion, owner
            // overrides, or the once-per-demo speed normalization identity.
            lock (_processBoundaryGate)
            {
                if (!IsProcessBoundaryCurrent(processBoundaryEpoch))
                    return;
                _ = _demoStartup.Observe(
                    new DemoStartupObservation(
                        false, false, string.Empty, false, false, false, false, false),
                    DateTimeOffset.UtcNow);
            }
            return;
        }
        // A failed F9/inverse profile remains an explicit Deadlock-mode debt.
        // Retry it on any healthy coordinator pass, including reconnects where
        // the replay itself stayed active, before startup may reapply SMVM.
        if (_controller.IsConnected && _deadlockUi.ProfileStatus.ShouldRetryRestore &&
            !RunForProcessBoundary(
                processBoundaryEpoch,
                _deadlockUi.RetryPendingRestore))
        {
            return;
        }
        if (_deadlockUi.ProfileStatus.ShouldRetryRestore)
            return;
        var replayTelemetryAuthoritative = HasAuthoritativeReplayTelemetry(replay);
        var replayIdentity = replayActive
            ? DemoStartupPolicy.CreateReplayIdentity(
                replay.ReplayName!,
                replay.TotalTicks,
                replay.ReplaySessionGeneration)
            : string.Empty;
        var presentation = _deadlockUi.ProfileStatus;
        if (replayTelemetryAuthoritative && replayActive &&
            presentation.ShouldRetryForwardProfile)
        {
            var forwardLease = CaptureForwardPresentationLease();
            var reapplied = await ApplyForwardPresentationWithLeaseAsync(
                forwardLease,
                stillCurrent => RunForProcessBoundary(
                    processBoundaryEpoch,
                    () => _deadlockUi.ApplyIfCurrent(
                        presentation.DesiredMode,
                        replayActive: true,
                        () => stillCurrent() &&
                              IsProcessBoundaryCurrent(processBoundaryEpoch))),
                "deferred recording-profile reassert",
                () => IsProcessBoundaryCurrent(processBoundaryEpoch)).ConfigureAwait(true);
            if (!reapplied || !IsProcessBoundaryCurrent(processBoundaryEpoch))
                return;
        }
        DemoStartupDirectives directives;
        lock (_processBoundaryGate)
        {
            if (!IsProcessBoundaryCurrent(processBoundaryEpoch))
                return;
            directives = _demoStartup.Observe(
                new DemoStartupObservation(
                    true,
                    replayActive,
                    replayIdentity,
                    replay.IsPaused == true,
                    _deadlockUi.State.Mode is DeadlockUiMode.SmvmReplayUi or DeadlockUiMode.CleanFootage,
                    _native.Connected,
                    _controller.GameTickOffset is not null,
                    _native.ManualCameraEstablished,
                    replayTelemetryAuthoritative,
                    replay.PlaybackHostActive),
                DateTimeOffset.UtcNow);
        }

        // A fresh VConsole connection publishes Connected=true before the
        // marker-fenced position response arrives. That provisional state must
        // not clear the same-demo startup owner override, custom speed identity,
        // or recording profile.
        if (!replayTelemetryAuthoritative)
            return;

        if (!replayActive)
        {
            lock (_processBoundaryGate)
            {
                if (!IsProcessBoundaryCurrent(processBoundaryEpoch))
                    return;
                _demoPlaybackSpeed.ClearForNoReplay();
                var profile = _deadlockUi.ProfileStatus;
                if (profile.RequiresRestore || profile.DesiredMode != DeadlockUiMode.DeadlockUi)
                    _deadlockUi.Restore();
            }
            return;
        }

        // Establish the owner's 100% demo-playback baseline once for every
        // newly observed replay, then leave later custom choices alone for
        // that replay session.
        lock (_processBoundaryGate)
        {
            if (!IsProcessBoundaryCurrent(processBoundaryEpoch))
                return;
            if (_demoPlaybackSpeed.TryClaimStartupNormalization(
                    replayIdentity,
                    playbackSpeedLease))
            {
                try
                {
                    if (!TryCaptureReplayCommandLease(replayIdentity, out var speedCommandLease))
                        throw new InvalidOperationException(
                            "Replay changed before startup playback speed normalization.");
                    // One-way migration from the earlier host-wide control. A
                    // running Deadlock process can retain that value across tool
                    // updates, so neutralize it before applying demo playback
                    // speed. Owner controls never write host_timescale afterward.
                    _controller.SendRaw(ReplayCommands.ResetLegacyHostTimescale);
                    if (!_controller.SetSpeedIfCurrent(1.0, speedCommandLease))
                        throw new InvalidOperationException(
                            "Replay changed while startup playback speed was being normalized.");
                    _log.Info(
                        "Demo startup: legacy host scaling neutralized and replay playback speed reset to 100%.");
                }
                catch (Exception ex)
                {
                    _demoPlaybackSpeed.MarkNormalizationFailed(
                        replayIdentity,
                        playbackSpeedLease);
                    _log.Warn($"Demo startup: replay playback speed reset failed: {ex.Message}");
                }
            }
        }

        if (directives.PauseDemo)
        {
            lock (_processBoundaryGate)
            {
                if (!IsProcessBoundaryCurrent(processBoundaryEpoch) ||
                    !_demoStartup.IsCurrent(directives.Generation))
                    return;
                try
                {
                    if (!TryCaptureReplayCommandLease(replayIdentity, out var pauseCommandLease) ||
                        !_controller.PauseIfCurrent(pauseCommandLease))
                    {
                        throw new InvalidOperationException(
                            "Replay changed before the startup pause command was issued.");
                    }
                    _log.Info("Demo startup: pause requested.");
                }
                catch (Exception ex)
                {
                    _demoStartup.MarkPauseCommandFailed(directives.Generation);
                    _log.Warn($"Demo startup: pause request failed: {ex.Message}");
                }
            }
        }

        if (directives.HideGameHud)
        {
            var forwardLease = CaptureForwardPresentationLease();
            var hidden = await ApplyForwardPresentationWithLeaseAsync(
                forwardLease,
                stillCurrent => RunForProcessBoundary(
                    processBoundaryEpoch,
                    () => _deadlockUi.ApplyIfCurrent(
                        DeadlockUiMode.SmvmReplayUi,
                        replayActive: true,
                        () => stillCurrent() &&
                              IsProcessBoundaryCurrent(processBoundaryEpoch))),
                "demo-startup recording profile",
                () => _demoStartup.IsCurrent(directives.Generation) &&
                      IsReplayIdentityCurrent(replayIdentity) &&
                      IsProcessBoundaryCurrent(processBoundaryEpoch)).ConfigureAwait(true);
            lock (_processBoundaryGate)
            {
                if (!IsProcessBoundaryCurrent(processBoundaryEpoch))
                    return;
                _demoStartup.MarkHudAttemptCompleted(
                    directives.Generation,
                    hidden,
                    DateTimeOffset.UtcNow);
                if (hidden)
                    _log.Info("Demo startup: Deadlock HUD hidden.");
            }
        }

        if (directives.EnterFreeCamera)
            _ = EnterAutomaticFreeCameraAsync(
                directives.Generation,
                processBoundaryEpoch,
                replayIdentity,
                CurrentPresentationReplayEpoch);
    }

    private async Task EnterAutomaticFreeCameraAsync(
        int generation,
        long processBoundaryEpoch,
        string replayIdentity,
        long presentationReplayEpoch)
    {
        var acquired = false;
        var success = false;
        try
        {
            await _actionGate.WaitAsync(_stop.Token).ConfigureAwait(true);
            acquired = true;
            Task enterTask;
            ReplayCommandLease replayCommandLease;
            lock (_processBoundaryGate)
            {
                if (!IsAutomaticStartupLeaseCurrent(
                        generation,
                        processBoundaryEpoch,
                        replayIdentity,
                        presentationReplayEpoch) ||
                    !TryCaptureReplayCommandLease(replayIdentity, out replayCommandLease))
                {
                    return;
                }
            }
            bool StillCurrent() => IsAutomaticStartupLeaseCurrent(
                generation,
                processBoundaryEpoch,
                replayIdentity,
                presentationReplayEpoch);
            using (_native.BeginQueuedActionLease(StillCurrent, replayCommandLease))
            {
                if (!StillCurrent())
                    return;
                enterTask = EnterSmvmFreeCameraAsync();
                await enterTask.ConfigureAwait(true);
            }
            lock (_processBoundaryGate)
            {
                if (!IsAutomaticStartupLeaseCurrent(
                        generation,
                        processBoundaryEpoch,
                        replayIdentity,
                        presentationReplayEpoch))
                {
                    return;
                }
                success = _native.ManualCameraEstablished;
                if (success)
                    _log.Info("Demo startup: SMVM Free Camera active; Deadlock gameplay input is suppressed.");
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"Demo startup: automatic Free Camera attempt failed: {ex.Message}");
        }
        finally
        {
            if (acquired)
                _actionGate.Release();
            lock (_processBoundaryGate)
            {
                if (IsProcessBoundaryCurrent(processBoundaryEpoch) &&
                    IsReplayIdentityCurrent(replayIdentity) &&
                    CurrentPresentationReplayEpoch == presentationReplayEpoch)
                {
                    _demoStartup.MarkFreeCameraAttemptCompleted(
                        generation,
                        success,
                        DateTimeOffset.UtcNow);
                }
            }
        }

        if (!success && IsAutomaticStartupLeaseCurrent(
                generation,
                processBoundaryEpoch,
                replayIdentity,
                presentationReplayEpoch) &&
            !_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, _stop.Token).ConfigureAwait(false);
                BeginInvokeIsolated(
                    () => _ = CoordinateDemoStartupAsync(_controller.State));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }
    }

    private static bool IsReplayActive(ReplayState replay) =>
        HasAuthoritativeReplayTelemetry(replay) &&
        (replay.TotalTicks is null || replay.CurrentTick < replay.TotalTicks);

    private static bool HasAuthoritativeReplayTelemetry(ReplayState replay) =>
        replay.Connected && !string.IsNullOrWhiteSpace(replay.ReplayName) &&
        replay.CurrentTick is not null;

    private EditorSnapshot ReadEditorSnapshot()
    {
        var keys = _campath.GetKeyframeSnapshot().ToArray();
        return new EditorSnapshot(
            _campath.PathName,
            _campath.Status,
            keys,
            _campath.SelectedKeyframe is { } selected ? Array.IndexOf(keys, selected) : -1,
            _campath.InterpolationMode,
            _campath.EasingMode,
            _campath.EndBehavior,
            _campath.SessionState,
            _campath.RecoveryAvailable,
            _campath.SavedCampaths.Count);
    }

    private void RefreshEditorSnapshot()
    {
        var next = ReadEditorSnapshot();
        lock (_editorGate)
        {
            var pathChanged = !_editor.Keyframes.SequenceEqual(next.Keyframes) ||
                              _editor.Interpolation != next.Interpolation ||
                              _editor.Easing != next.Easing;
            _editor = next;
            if (pathChanged)
                _editorRevision++;
        }
    }

    private SmvmSnapshot CreateSnapshot()
    {
        EditorSnapshot editor;
        lock (_editorGate)
            editor = _editor;
        var replay = _controller.State;
        var native = _native.Status;
        var playback = _native.CampathStatus;
        var replaySeekInProgress =
            Volatile.Read(ref _replaySeekInFlight) != 0 || IsReplaySeeking(playback.State);
        var internalEnabled = true; // The internal SMVM editor is the only editor.
        var replayActive = IsReplayActive(replay);
        // Read the managed presentation transaction atomically. Mode, debt,
        // and transaction-in-progress must describe the same point in time so native
        // recovery cannot mistake a healthy four-command apply for a failure.
        var presentation = _deadlockUi.ProfileStatus;
        var deadlockUi = presentation.Ui;
        var ownership = ResolveCameraOwnership(replayActive, native);
        var availability = ResolveCameraAvailability(
            internalEnabled, replayActive, replay, native, playback, ownership);
        var capabilities = ResolveCapabilities(internalEnabled, native);
        var cameraOwnershipIntent = replayActive && _native.CameraOwned;
        var cameraReadable = replayActive && _native.Connected &&
            (native?.CameraObserved == true ||
             (native?.Camera.IsValid == true &&
              ownership is CameraOwnership.SmvmManualCamera or
                  CameraOwnership.SmvmRestore or CameraOwnership.SmvmCampath));
        var rollWritable = availability == CameraAvailability.Ready &&
                           ownership == CameraOwnership.SmvmManualCamera &&
                           _native.ManualCameraEstablished;

        var flags = SmvmSnapshotFlags.None;
        if (replayActive) flags |= SmvmSnapshotFlags.ReplayActive;
        if (replay.IsPaused is not null) flags |= SmvmSnapshotFlags.PauseKnown;
        if (replay.IsPaused == true) flags |= SmvmSnapshotFlags.Paused;
        if (cameraReadable) flags |= SmvmSnapshotFlags.CameraReadable;
        if (cameraReadable && _native.Available && rollWritable)
            flags |= SmvmSnapshotFlags.RollWritable;
        if (ownership == CameraOwnership.SmvmCampath) flags |= SmvmSnapshotFlags.CampathPlaying;
        // CameraOwned is the existing protocol flag for managed ownership
        // intent. It deliberately includes a desired-but-recovering Free Camera,
        // while ManualCameraRequested/Active retain their native meanings.
        if (cameraOwnershipIntent)
            flags |= SmvmSnapshotFlags.CameraOwned;
        if (editor.Keyframes.Length > 0)
        {
            flags |= SmvmSnapshotFlags.EditorPath;
            flags |= SmvmSnapshotFlags.ShowCameras;
            if (editor.Keyframes.Length > 1)
                flags |= SmvmSnapshotFlags.ShowPath;
        }
        if (internalEnabled) flags |= SmvmSnapshotFlags.InternalEnabled;
        // Keep legacy visibility settings load-compatible without publishing
        // their toolbar, labels, pill, toast, or HUD flags into the live product.
        // Camera placement markers and their connecting path are always-on
        // editor guides now; they are not another settings decision.
        if (_settings.SmvmFovWheelInverted) flags |= SmvmSnapshotFlags.FovInverted;
        if (replayActive && _native.Connected && native?.ManualCameraRequested == true)
            flags |= SmvmSnapshotFlags.ManualCameraRequested;
        if (replayActive && _native.Connected && _native.ManualCameraActive)
            flags |= SmvmSnapshotFlags.ManualCameraActive;
        // SMVM Free Camera is the single user-facing camera. Suppressing
        // Deadlock's competing observer input is an invariant for manual
        // acquisition/recovery and for full path/restore ownership.
        if (cameraOwnershipIntent)
            flags |= SmvmSnapshotFlags.InputTakeover;
        if (_settings.SmvmMouseInvertY) flags |= SmvmSnapshotFlags.InvertY;
        if (editor.Session == CampathSessionState.DraftPath) flags |= SmvmSnapshotFlags.CampathUnsaved;
        if (editor.RecoveryAvailable) flags |= SmvmSnapshotFlags.CampathRecoveryAvailable;
        if (_settings.RestoreLastWorkspace) flags |= SmvmSnapshotFlags.RestoreWorkspace;
        if (_captureDiagnosticsEnabled) flags |= SmvmSnapshotFlags.CaptureDiagnostics;
        if (replaySeekInProgress) flags |= SmvmSnapshotFlags.ReplaySeekInProgress;
        if (presentation.RestorePending)
            flags |= SmvmSnapshotFlags.RecordingProfileRestorePending;
        if (presentation.TransactionInProgress)
            flags |= SmvmSnapshotFlags.RecordingProfileTransactionInProgress;

        return new SmvmSnapshot(
            flags,
            replay.CurrentTick ?? -1,
            replay.TotalTicks ?? -1,
            replay.Timescale ?? 1.0,
            cameraReadable ? native!.Camera : default,
            _camera.Selection.Mode,
            editor.SelectedIndex,
            editor.Keyframes.Length,
            editor.Interpolation,
            editor.Easing,
            editor.EndBehavior,
            playback.State,
            playback.Failure,
            SmvmInputCode.ParseOrDefault(_settings.SmvmMenuHotkey, 0x09),
            SmvmInputCode.ParseOrDefault(_settings.SmvmAddHotkey, SmvmInputCode.MouseMiddle),
            SmvmInputCode.ParseOrDefault(_settings.SmvmDeleteHotkey, (uint)'L'),
            SmvmInputCode.ParseOrDefault(_settings.SmvmCleanViewHotkey, 0x79),
            _settings.SmvmFovWheelStep,
            SmvmInputCode.ParseForSlotOrDefault(108, _settings.SmvmRollLeftHotkey, "Q"),
            SmvmInputCode.ParseForSlotOrDefault(109, _settings.SmvmRollRightHotkey, "E"),
            SmvmInputCode.ParseForSlotOrDefault(110, _settings.SmvmRollResetHotkey, "R"),
            availability,
            ownership,
            capabilities,
            SmvmInputCode.ParseForSlotOrDefault(100, _settings.SmvmForwardHotkey, "W"),
            SmvmInputCode.ParseForSlotOrDefault(101, _settings.SmvmBackHotkey, "S"),
            SmvmInputCode.ParseForSlotOrDefault(102, _settings.SmvmLeftHotkey, "A"),
            SmvmInputCode.ParseForSlotOrDefault(103, _settings.SmvmRightHotkey, "D"),
            SmvmInputCode.ParseForSlotOrDefault(104, _settings.SmvmUpHotkey, "Space"),
            SmvmInputCode.ParseForSlotOrDefault(105, _settings.SmvmDownHotkey, "VK11"),
            SmvmInputCode.ParseForSlotOrDefault(106, _settings.SmvmFastHotkey, "VK10"),
            SmvmInputCode.ParseForSlotOrDefault(107, _settings.SmvmPrecisionHotkey, "VK12"),
            ParseBinding(_settings.SmvmPlayStartHotkey, string.Empty),
            ParseBinding(_settings.SmvmPlayCurrentHotkey, string.Empty),
            ParseBinding(_settings.SmvmStopHotkey, string.Empty),
            ParseBinding(_settings.SmvmUndoHotkey, "Ctrl+Z"),
            ParseBinding(_settings.SmvmRedoHotkey, "Ctrl+Y"),
            ParseBinding(_settings.SmvmShowPathHotkey, string.Empty),
            ParseBinding(_settings.SmvmShowCamerasHotkey, string.Empty),
            ParseBinding(_settings.SmvmShowLabelsHotkey, string.Empty),
            _settings.SmvmMovementSpeed,
            _settings.SmvmMovementBoost,
            _settings.SmvmMovementPrecision,
            _settings.SmvmMouseSensitivity,
            _settings.SmvmMouseSmoothing,
            _settings.SmvmUiScale,
            _settings.SmvmOpacity,
            _settings.SmvmPathLabelScale,
            _settings.SmvmMenuAnchor,
            _settings.SmvmNotificationAnchor,
            replay.ReplayName ?? string.Empty,
            editor.Name,
            _native.SelfTestStatus.IsRunning ||
            _native.SelfTestStatus.Stage is SmvmSelfTestStage.Completed or SmvmSelfTestStage.Failed or SmvmSelfTestStage.Cancelled
                ? _native.SelfTestStatus.Detail
                : !string.IsNullOrWhiteSpace(editor.Status) ? editor.Status : _native.Message,
            DescribeCameraAvailability(availability),
            editor.Session,
            editor.SavedDocumentCount,
            SmvmInputCode.ParseForSlotOrDefault(122, _settings.SmvmRestoreUiHotkey, "F9"),
            deadlockUi.Mode,
            deadlockUi.Capabilities,
            deadlockUi.Error,
            _settings.VConsolePort,
            _settings.SmvmReplayBarScale,
            _settings.SmvmReplayBarOpacity,
            _settings.SmvmReplayBarAnchor,
            SmvmInputCode.ParseForSlotOrDefault(123, _settings.SmvmCycleUiHotkey, "F8"),
            SmvmInputCode.ParseForSlotOrDefault(124, _settings.SmvmToggleFreeCameraHotkey, "F2"),
            SmvmInputCode.ParseForSlotOrDefault(125, _settings.SmvmReplayPauseHotkey, "N"),
            SmvmInputCode.ParseForSlotOrDefault(127, _settings.SmvmStepBackHotkey, string.Empty),
            SmvmInputCode.ParseForSlotOrDefault(128, _settings.SmvmStepForwardHotkey, string.Empty),
            _settings.SmvmStatusHudAnchor,
            _settings.SmvmStatusHudScale,
            _settings.SmvmStatusHudOpacity,
            presentation.AcknowledgementGeneration,
            replay.ReplaySessionGeneration);
    }

    private CameraOwnership ResolveCameraOwnership(bool replayActive, InProcessCameraStatus? native)
    {
        if (!replayActive)
            return CameraOwnership.None;
        if (!_native.Connected)
            return CameraOwnership.DeadlockSpectator;
        if (_native.CampathPlaying || native?.Flags.HasFlag(InProcessStatusFlags.CampathActive) == true)
            return CameraOwnership.SmvmCampath;
        if (native?.OverrideActive == true || _native.CampathCameraOwned)
            return CameraOwnership.SmvmRestore;
        if (native?.ManualCameraRequested == true || native?.ManualCameraActive == true)
            return CameraOwnership.SmvmManualCamera;
        return CameraOwnership.DeadlockSpectator;
    }

    private CameraAvailability ResolveCameraAvailability(
        bool internalEnabled,
        bool replayActive,
        ReplayState replay,
        InProcessCameraStatus? native,
        CampathPlaybackStatus playback,
        CameraOwnership ownership)
    {
        if (!internalEnabled || !replay.Connected)
            return CameraAvailability.ManagedHostDisconnected;
        if (!replayActive)
            return CameraAvailability.ReplayUnavailable;
        if (Volatile.Read(ref _replaySeekInFlight) != 0 || IsReplaySeeking(playback.State))
            return CameraAvailability.ReplaySeeking;
        if (native?.State == InProcessBackendState.Loading)
            return CameraAvailability.Initializing;
        if (native is not null && MapNativeAvailability(native.Error) is { } nativeFailure)
            return nativeFailure;
        if (!_native.Connected)
            return CameraAvailability.NativeBackendDisconnected;
        if (native is null || native.State == InProcessBackendState.Connected)
            return CameraAvailability.Initializing;
        if (native.State is InProcessBackendState.Unavailable or InProcessBackendState.Failed)
            return CameraAvailability.CameraManagerUnavailable;
        if (!native.Flags.HasFlag(InProcessStatusFlags.Resolved))
            return CameraAvailability.Initializing;
        if (!native.Flags.HasFlag(InProcessStatusFlags.HookInstalled))
            return CameraAvailability.CameraManagerUnavailable;
        if (_camera.Selection.PlayerSlot is not null ||
            _camera.Selection.Mode is SpecCameraMode.InEye or SpecCameraMode.Chase)
            return CameraAvailability.ObserverTargetActive;
        if (_camera.Selection.Mode != SpecCameraMode.FreeRoam)
            return CameraAvailability.NotInFreeRoam;
        if (ownership == CameraOwnership.SmvmCampath)
            return CameraAvailability.OwnershipHeldByCampath;
        if (_native.ManualCameraDesired && !native.ManualCameraActive)
            return CameraAvailability.Initializing;
        if (ownership == CameraOwnership.SmvmRestore)
            return native?.OverrideActive == true
                ? CameraAvailability.Ready
                : CameraAvailability.OwnershipRejected;
        if (!native.CameraObserved)
            return CameraAvailability.CameraReadbackUnavailable;
        return CameraAvailability.Ready;
    }

    private SmvmCapabilities ResolveCapabilities(bool internalEnabled, InProcessCameraStatus? native) =>
        SmvmCapabilityResolver.Resolve(internalEnabled, _native.Connected, native, _native.SelfTestStatus.IsRunning);

    private static bool IsReplaySeeking(CampathPlaybackState state) =>
        state is CampathPlaybackState.SeekingToStart or CampathPlaybackState.WaitingForLandedTick or
            CampathPlaybackState.ReacquiringFreeRoam;

    private static CameraAvailability? MapNativeAvailability(InProcessErrorCode error) => error switch
    {
        InProcessErrorCode.None => null,
        InProcessErrorCode.WrongProcess or InProcessErrorCode.ReplayLaunchRequired or
            InProcessErrorCode.ReplayGateClosed or InProcessErrorCode.ReplayClockUnavailable =>
            CameraAvailability.ReplayUnavailable,
        InProcessErrorCode.ClientModuleMissing or InProcessErrorCode.SignatureMissing or
            InProcessErrorCode.SignatureAmbiguous or InProcessErrorCode.HookTargetMismatch or
            InProcessErrorCode.HookInstallFailed => CameraAvailability.SignatureUnavailable,
        InProcessErrorCode.CameraUnavailable => CameraAvailability.CameraObjectUnavailable,
        InProcessErrorCode.ProtocolError => CameraAvailability.ProtocolMismatch,
        InProcessErrorCode.InvalidSample => CameraAvailability.CameraReadbackUnavailable,
        InProcessErrorCode.ObserverNotRoaming => CameraAvailability.NotInFreeRoam,
        InProcessErrorCode.HeartbeatStale => CameraAvailability.SnapshotStale,
        InProcessErrorCode.HookRuntimeInvalid => CameraAvailability.CameraManagerUnavailable,
        _ => CameraAvailability.CameraManagerUnavailable,
    };

    private static string DescribeCameraAvailability(CameraAvailability availability) => availability switch
    {
        CameraAvailability.Initializing => "Camera initializing.",
        CameraAvailability.Ready => "Camera ready.",
        CameraAvailability.ReplayUnavailable => "Replay unavailable.",
        CameraAvailability.ReplaySeeking => "Replay seek in progress.",
        CameraAvailability.NotInFreeRoam => "Switch to Free Roam.",
        CameraAvailability.ObserverTargetActive => "Release the observer target.",
        CameraAvailability.CameraManagerUnavailable => "Camera manager unavailable.",
        CameraAvailability.CameraObjectUnavailable => "Camera object unavailable.",
        CameraAvailability.CameraReadbackUnavailable => "Camera readback unavailable.",
        CameraAvailability.NativeBackendDisconnected => "Native backend disconnected.",
        CameraAvailability.SignatureUnavailable => "Camera signatures unavailable.",
        CameraAvailability.ManagedHostDisconnected => "Managed host disconnected.",
        CameraAvailability.OwnershipHeldByCampath => "Campath owns the camera.",
        CameraAvailability.OwnershipRejected => "Camera ownership rejected.",
        CameraAvailability.SnapshotStale => "Camera snapshot stale.",
        CameraAvailability.ProtocolMismatch => "Protocol mismatch.",
        _ => "Camera unavailable.",
    };

    private static uint ParseBinding(string? binding, string fallback) =>
        SmvmInputCode.ParseOrDefault(binding, SmvmInputCode.ParseOrDefault(fallback, SmvmInputCode.None));

    private async Task PublishEditorPathAsync()
    {
        Interlocked.Exchange(ref _pathPublishPending, 1);
        var acquired = false;
        try
        {
            acquired = await _pathPublishGate.WaitAsync(0, _stop.Token).ConfigureAwait(false);
            if (!acquired)
                return;
            while (Interlocked.Exchange(ref _pathPublishPending, 0) != 0 &&
                   !_stop.IsCancellationRequested)
            {
                if (!_native.Connected)
                {
                    _publishedRevision = -1;
                    return;
                }
                while (!_stop.IsCancellationRequested)
                {
                    EditorSnapshot editor;
                    int revision;
                    lock (_editorGate)
                    {
                        editor = _editor;
                        revision = _editorRevision;
                    }
                    if (revision == _publishedRevision && _native.Connected)
                        break;
                    await _native.PublishEditorCampathAsync(
                        editor.Keyframes,
                        editor.Interpolation,
                        editor.Easing,
                        _stop.Token).ConfigureAwait(false);
                    _publishedRevision = revision;
                    lock (_editorGate)
                    {
                        if (revision == _editorRevision)
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _log.Warn($"SMVM editor-path synchronization deferred: {ex.Message}");
            _publishedRevision = -1;
        }
        finally
        {
            if (acquired)
                _pathPublishGate.Release();
            if (acquired && Volatile.Read(ref _pathPublishPending) != 0 &&
                !_stop.IsCancellationRequested)
                _ = PublishEditorPathAsync();
        }
    }

    private async Task ExecuteActionSafeAsync(
        SmvmAction action,
        SmvmQueuedActionLease queuedActionLease,
        long presentationIntentEpoch,
        DemoPlaybackSpeedActionLease playbackSpeedActionLease)
    {
        var acquired = false;
        try
        {
            await _actionGate.WaitAsync(_stop.Token).ConfigureAwait(true);
            acquired = true;
            bool QueuedActionStillCurrent() =>
                IsQueuedActionLeaseCurrent(action, queuedActionLease);
            if (!QueuedActionStillCurrent())
            {
                _log.Info($"Ignored queued SMVM action {action.Type}: its process, native, or replay lease expired.");
                return;
            }
            if (action.Type == SmvmActionType.SetTimescale)
            {
                try
                {
                    if (!TryExecutePlaybackSpeedAction(
                            action.Value,
                            playbackSpeedActionLease,
                            queuedActionLease))
                        ReleaseFailedPlaybackSpeedIntent(playbackSpeedActionLease);
                }
                catch
                {
                    ReleaseFailedPlaybackSpeedIntent(playbackSpeedActionLease);
                    throw;
                }
                return;
            }
            var replayCommandLease = SmvmQueuedActionLeasePolicy.RequiresReplayLease(action)
                ? new ReplayCommandLease(
                    queuedActionLease.ReplayConnectionGeneration,
                    queuedActionLease.ReplaySessionGeneration,
                    queuedActionLease.ReplayName)
                : (ReplayCommandLease?)null;
            var resolvedPresentationTarget = ResolvePresentationTarget(action);
            var requiresPresentationLease =
                resolvedPresentationTarget is not null and not DeadlockUiMode.DeadlockUi;
            var forwardLease = new ForwardPresentationLease(
                queuedActionLease.NativeConnectionEpoch,
                presentationIntentEpoch,
                queuedActionLease.PresentationReplayEpoch);
            if (requiresPresentationLease &&
                !await ConfirmPresentationConnectionLeaseAsync(
                    forwardLease.ConnectionEpoch,
                    $"queued {action.Type}").ConfigureAwait(true))
                return;
            if (requiresPresentationLease &&
                (!IsForwardPresentationLeaseCurrent(forwardLease) ||
                 !QueuedActionStillCurrent()))
                return;

            Func<bool>? forwardStillCurrent = requiresPresentationLease
                ? () => IsForwardPresentationLeaseCurrent(forwardLease) &&
                        QueuedActionStillCurrent()
                : null;
            using (_native.BeginQueuedActionLease(
                       QueuedActionStillCurrent,
                       replayCommandLease))
            {
                if (!QueuedActionStillCurrent())
                    return;
                await ExecuteActionAsync(
                    action,
                    resolvedPresentationTarget,
                    forwardStillCurrent,
                    QueuedActionStillCurrent,
                    replayCommandLease).ConfigureAwait(true);
            }

            if (requiresPresentationLease)
            {
                if (!IsForwardPresentationLeaseCurrent(forwardLease) ||
                    !QueuedActionStillCurrent())
                {
                    RestoreAfterInvalidForwardPresentationLease(forwardLease);
                }
                else if (!await ConfirmPresentationConnectionLeaseAsync(
                             forwardLease.ConnectionEpoch,
                             $"completed {action.Type}").ConfigureAwait(true))
                {
                    // A host-loss inverse may have raced this independent
                    // VConsole forward batch. End with the inverse again; the
                    // new native connection generation owns any later reassert.
                    _deadlockUi.RestoreAfterConnectionLeaseLoss();
                }
                else if (!IsForwardPresentationLeaseCurrent(forwardLease) ||
                         !QueuedActionStillCurrent())
                {
                    // GetStatus can itself deliver F9. Never let that ingress
                    // restore be the middle rather than the end of the profile.
                    RestoreAfterInvalidForwardPresentationLease(forwardLease);
                }
            }
            if (!QueuedActionStillCurrent())
                return;
            RefreshEditorSnapshot();
            _ = PublishEditorPathAsync();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"SMVM action {action.Type} failed: {ex.Message}");
        }
        finally
        {
            if (acquired)
                _actionGate.Release();
        }
    }

    private bool IsQueuedActionLeaseCurrent(
        SmvmAction action,
        SmvmQueuedActionLease lease)
    {
        var replaySnapshot = _controller.CaptureReplayTelemetrySnapshot();
        var replay = replaySnapshot.State;
        var nativeLeaseCurrent = _native.IsConnectionLeaseCurrent(lease.NativeConnectionEpoch);
        return SmvmQueuedActionLeasePolicy.IsCurrent(
            action,
            lease,
            Volatile.Read(ref _processBoundaryEpoch),
            nativeLeaseCurrent ? lease.NativeConnectionEpoch : _native.ConnectionEpoch,
            CurrentPresentationReplayEpoch,
            replaySnapshot.ConnectionGeneration,
            replay.ReplaySessionGeneration,
            replay.ReplayName,
            nativeLeaseCurrent,
            HasAuthoritativeReplayTelemetry(replay));
    }

    private async Task ExecuteActionAsync(
        SmvmAction action,
        DeadlockUiMode? resolvedPresentationTarget,
        Func<bool>? forwardStillCurrent,
        Func<bool> actionStillCurrent,
        ReplayCommandLease? replayCommandLease)
    {
        if (!actionStillCurrent())
            return;
        bool RunReplayEffect(Action effect)
        {
            if (replayCommandLease is { } lease)
                return _controller.RunIfCurrent(lease, effect);
            if (!actionStillCurrent())
                return false;
            effect();
            return true;
        }
        switch (action.Type)
        {
            case SmvmActionType.CaptureDiagnostic:
            {
                var stage = (SmvmCaptureStage)action.Index;
                var rejection = (SmvmCaptureRejection)action.Tick;
                _ = RunReplayEffect(() =>
                {
                    TraceCapture(stage, rejection);
                    if (stage == SmvmCaptureStage.CaptureRejected)
                        _campath.ReportCaptureRejection(rejection);
                });
                break;
            }
            case SmvmActionType.SetDeadlockUiMode:
                if (action.Tick == 0)
                {
                    // Tab/menu reassertion carries no owner generation. Reapply
                    // whichever mode is current when this FIFO action executes;
                    // never let a stale SMVM target supersede a newer owner
                    // transition that was queued immediately before it.
                    ReassertDeadlockUiIfCurrent(actionStillCurrent);
                    break;
                }
                ApplyDeadlockUiMode(
                    resolvedPresentationTarget ?? (DeadlockUiMode)action.Index,
                    RecoveryGeneration(action),
                    forwardStillCurrent ?? actionStillCurrent);
                break;
            case SmvmActionType.RestoreDeadlockUi:
                var exitTask = ExitSmvmFreeCameraAsync();
                try
                {
                    try
                    {
                        await exitTask.WaitAsync(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(true);
                    }
                    catch (TimeoutException)
                    {
                        // F9 is the emergency presentation escape. Do not cancel
                        // the ownership release: let it finish behind any active
                        // seek while restoring Deadlock UI immediately.
                        _log.Warn("SMVM camera release is still completing after F9; Deadlock UI was restored immediately.");
                        _ = ObserveDeferredCameraExitAsync(exitTask);
                    }
                }
                finally
                {
                    if (actionStillCurrent())
                    {
                        ApplyDeadlockUiMode(
                            DeadlockUiMode.DeadlockUi,
                            RecoveryGeneration(action),
                            actionStillCurrent);
                    }
                }
                break;
            case SmvmActionType.CycleReplayInterface:
            {
                ApplyDeadlockUiMode(
                    resolvedPresentationTarget ?? DeadlockUiMode.DeadlockUi,
                    stillCurrent: forwardStillCurrent ?? actionStillCurrent);
                break;
            }
            case SmvmActionType.SetReplayBarScale:
                _settings.SmvmReplayBarScale = action.Value;
                _settings.Save();
                break;
            case SmvmActionType.SetReplayBarOpacity:
                _settings.SmvmReplayBarOpacity = action.Value;
                _settings.Save();
                break;
            case SmvmActionType.SetReplayBarAnchor:
                if (Enum.IsDefined((SmvmReplayBarAnchor)action.Index))
                {
                    _settings.SmvmReplayBarAnchor = (SmvmReplayBarAnchor)action.Index;
                    _settings.Save();
                }
                break;
            case SmvmActionType.ToggleStatusHud:
                _settings.SmvmShowStatusHud = !_settings.SmvmShowStatusHud;
                _settings.Save();
                break;
            case SmvmActionType.SetStatusHudAnchor:
                if (Enum.IsDefined((SmvmNotificationAnchor)action.Index))
                {
                    _settings.SmvmStatusHudAnchor = (SmvmNotificationAnchor)action.Index;
                    _settings.Save();
                }
                break;
            case SmvmActionType.SetStatusHudScale:
                _settings.SmvmStatusHudScale = action.Value;
                _settings.Save();
                break;
            case SmvmActionType.SetStatusHudOpacity:
                _settings.SmvmStatusHudOpacity = action.Value;
                _settings.Save();
                break;
            case SmvmActionType.ToggleReplayPause:
                if (replayCommandLease is not { } pauseLease)
                    return;
                var pauseCommandIssued = action.Index switch
                {
                    1 => _controller.PauseIfCurrent(pauseLease),
                    0 => _controller.PlayIfCurrent(pauseLease),
                    _ => _controller.TogglePauseIfCurrent(pauseLease),
                };
                if (!pauseCommandIssued || !actionStillCurrent())
                    return;
                // Send suppression after the transport command in the same
                // VConsole ordering window. The observed pause-state edge
                // reasserts once more after the engine settles.
                if (resolvedPresentationTarget is { } pauseTarget &&
                    pauseTarget != DeadlockUiMode.DeadlockUi)
                {
                    ApplyDeadlockUiMode(
                        pauseTarget,
                        stillCurrent: forwardStillCurrent);
                }
                break;
            case SmvmActionType.SeekTick:
                await ExecuteReplaySeekAsync(
                    checked((int)Math.Clamp(action.Tick, 0, int.MaxValue))).ConfigureAwait(true);
                break;
            case SmvmActionType.StepBack:
                if (_controller.State.CurrentTick is { } currentTick && currentTick > 0)
                    await ExecuteReplaySeekAsync(currentTick - 1).ConfigureAwait(true);
                break;
            case SmvmActionType.StepForward:
                if (_controller.State.CurrentTick is { } nextTick && nextTick < int.MaxValue)
                    await ExecuteReplaySeekAsync(nextTick + 1).ConfigureAwait(true);
                break;
            case SmvmActionType.FreeRoam:
                await EnterSmvmFreeCameraAsync().ConfigureAwait(true);
                break;
            case SmvmActionType.PreviousPlayer:
                await ExitSmvmFreeCameraAsync().ConfigureAwait(true);
                if (!RunReplayEffect(_camera.SelectPrevPlayer))
                    return;
                break;
            case SmvmActionType.NextPlayer:
                await ExitSmvmFreeCameraAsync().ConfigureAwait(true);
                if (!RunReplayEffect(_camera.SelectNextPlayer))
                    return;
                break;
            case SmvmActionType.InEye:
                await ExitSmvmFreeCameraAsync().ConfigureAwait(true);
                if (!RunReplayEffect(_camera.SelectInEye))
                    return;
                break;
            case SmvmActionType.Chase:
                await ExitSmvmFreeCameraAsync().ConfigureAwait(true);
                if (!RunReplayEffect(_camera.SelectChase))
                    return;
                break;
            case SmvmActionType.SetFov:
                // A stale overlay may still send this legacy action. The
                // external Deadlock FOV backend cannot update the in-process
                // manual sample, so fail closed until a true manual setter exists.
                break;
            case SmvmActionType.SaveCamera:
            case SmvmActionType.RestoreCamera:
                // Snapshot utilities were removed from the product workflow.
                // Keep their protocol values as inert compatibility entries.
                break;
            case SmvmActionType.AddKeyframe:
                _ = RunReplayEffect(() =>
                {
                    if (action.Tick < 0 || !action.Camera.IsValid)
                        return;
                    if (!_native.ManualCameraEstablished)
                    {
                        TraceCapture(
                            SmvmCaptureStage.CaptureRejected,
                            SmvmCaptureRejection.NotInFreeRoam,
                            "SMVM Free Camera is not active");
                        _campath.ReportCaptureRejection(SmvmCaptureRejection.NotInFreeRoam);
                        return;
                    }
                    TraceCapture(
                        SmvmCaptureStage.ManagedActionReturned,
                        detail: $"tick={action.Tick}, camera=({action.Camera.X:F3}, {action.Camera.Y:F3}, {action.Camera.Z:F3}), " +
                                $"rot=({action.Camera.Pitch:F3}, {action.Camera.Yaw:F3}, {action.Camera.Roll:F3}), fov={action.Camera.Fov:F3}");
                    var priorSession = _campath.SessionState;
                    _campath.AddAuthoritativeKeyframe(new CampathKeyframe(action.Tick, action.Camera));
                    if (priorSession == CampathSessionState.NoPath &&
                        _campath.SessionState == CampathSessionState.DraftPath)
                        TraceCapture(SmvmCaptureStage.DraftCreated);
                    TraceCapture(
                        SmvmCaptureStage.KeyframeAdded,
                        detail: $"count={_campath.Keyframes.Count}, tick={action.Tick}");
                });
                break;
            case SmvmActionType.DeleteKeyframe:
                _ = RunReplayEffect(() => _campath.DeleteKeyframe(action.Index));
                break;
            case SmvmActionType.SelectKeyframe:
                _ = RunReplayEffect(() => _campath.SelectKeyframe(action.Index));
                break;
            case SmvmActionType.GoToKeyframe:
                await EnterSmvmFreeCameraAsync().ConfigureAwait(true);
                if (!RunReplayEffect(() => _campath.SelectKeyframe(action.Index)))
                    return;
                await ExecuteKeyframeSeekAsync().ConfigureAwait(true);
                break;
            case SmvmActionType.UpdateKeyframe:
                if (!RunReplayEffect(() => _campath.SelectKeyframe(action.Index)))
                    return;
                await _campath.UpdateAsync(
                    actionStillCurrent,
                    effect => RunReplayEffect(effect)).ConfigureAwait(true);
                break;
            case SmvmActionType.ClearPath:
                _ = RunReplayEffect(_campath.RequestClear);
                break;
            case SmvmActionType.SetInterpolation:
                _ = RunReplayEffect(() =>
                    _campath.InterpolationMode = action.Index == 1
                        ? CampathInterpolationMode.Smooth
                        : CampathInterpolationMode.Linear);
                break;
            case SmvmActionType.SetEasing:
                if (Enum.IsDefined((CampathEasingMode)action.Index))
                    _ = RunReplayEffect(() => _campath.EasingMode = (CampathEasingMode)action.Index);
                break;
            case SmvmActionType.PlayFromStart:
                await EnterSmvmFreeCameraAsync().ConfigureAwait(true);
                if (!actionStillCurrent())
                    return;
                await _campath.PlayAsync(CampathPlayMode.FromStart).ConfigureAwait(true);
                break;
            case SmvmActionType.PlayFromCurrent:
                await EnterSmvmFreeCameraAsync().ConfigureAwait(true);
                if (!actionStillCurrent())
                    return;
                await _campath.PlayAsync(CampathPlayMode.FromCurrent).ConfigureAwait(true);
                break;
            case SmvmActionType.StopCampath:
                await _campath.StopAsync().ConfigureAwait(true);
                break;
            case SmvmActionType.SetEndBehavior:
                _ = RunReplayEffect(() =>
                    _campath.EndBehavior = action.Index == 1
                        ? CampathEndBehavior.HoldFinalCamera
                        : CampathEndBehavior.StopAndRelease);
                break;
            case SmvmActionType.UndoEdit:
                _ = RunReplayEffect(() => _campath.UndoCommand.Execute(null));
                break;
            case SmvmActionType.RedoEdit:
                _ = RunReplayEffect(() => _campath.RedoCommand.Execute(null));
                break;
            case SmvmActionType.ToggleToolbar:
                _settings.SmvmShowToolbar = !_settings.SmvmShowToolbar;
                _settings.Save();
                break;
            case SmvmActionType.ToggleShowPath:
                _settings.SmvmShowPath = !_settings.SmvmShowPath;
                _settings.Save();
                break;
            case SmvmActionType.ToggleShowCameras:
                _settings.SmvmShowCameras = !_settings.SmvmShowCameras;
                _settings.Save();
                break;
            case SmvmActionType.ToggleShowLabels:
                _settings.SmvmShowLabels = !_settings.SmvmShowLabels;
                _settings.Save();
                break;
            case SmvmActionType.SetBinding when action.Index == 4:
                _settings.SmvmFovWheelStep = action.Value;
                _settings.Save();
                break;
            case SmvmActionType.SetBinding:
                SetSmvmBinding(action.Index, action.Value);
                break;
            case SmvmActionType.SetPathName:
                if (!string.IsNullOrWhiteSpace(action.Text))
                    _ = RunReplayEffect(() => _campath.PathName = action.Text);
                break;
            case SmvmActionType.SavePath:
                _ = RunReplayEffect(_campath.SaveCurrent);
                break;
            case SmvmActionType.LoadNextPath:
                _ = RunReplayEffect(_campath.LoadNextMatching);
                break;
            case SmvmActionType.NewPath:
                _ = RunReplayEffect(_campath.NewPath);
                break;
            case SmvmActionType.SavePathAs:
                _ = RunReplayEffect(() => _campath.SaveAs(action.Text));
                break;
            case SmvmActionType.LoadPath:
                _ = RunReplayEffect(() => _campath.LoadPathByIndex(action.Index));
                break;
            case SmvmActionType.ClosePath:
                _ = RunReplayEffect(_campath.ClosePath);
                break;
            case SmvmActionType.RecoverDraft:
                _ = RunReplayEffect(_campath.RecoverDraft);
                break;
            case SmvmActionType.DiscardDraft:
                _ = RunReplayEffect(_campath.DiscardDraft);
                break;
            case SmvmActionType.RequestPathList:
                _ = RunReplayEffect(() => _ = PublishDocumentsAsync());
                break;
            case SmvmActionType.ToggleRestoreWorkspace:
                _settings.RestoreLastWorkspace = !_settings.RestoreLastWorkspace;
                _settings.Save();
                break;
            case SmvmActionType.SetPathLabelScale:
                _settings.SmvmPathLabelScale = Math.Clamp(action.Value, 0.5, 2.0);
                _settings.Save();
                break;
            case SmvmActionType.SetNotificationAnchor:
                if (Enum.IsDefined((SmvmNotificationAnchor)action.Index))
                {
                    _settings.SmvmNotificationAnchor = (SmvmNotificationAnchor)action.Index;
                    _settings.Save();
                }
                break;
            case SmvmActionType.SetRoll:
                if (!_native.CampathPlaying)
                    await _native.SetManualRollAsync(Math.Clamp(action.Value, -180.0, 180.0), _stop.Token)
                        .ConfigureAwait(true);
                break;
            case SmvmActionType.ToggleNotifications:
                _settings.SmvmNotificationsEnabled = !_settings.SmvmNotificationsEnabled;
                _settings.Save();
                break;
            case SmvmActionType.ToggleInputTakeover:
                // Kept for protocol compatibility with older overlays. Camera
                // input takeover is now mandatory while Free Camera is active.
                _settings.SmvmCameraInputTakeover = true;
                _settings.Save();
                break;
            case SmvmActionType.SetUiScale:
                _settings.SmvmUiScale = action.Value;
                _settings.Save();
                break;
            case SmvmActionType.SetMenuOpacity:
                _settings.SmvmOpacity = action.Value;
                _settings.Save();
                break;
            case SmvmActionType.SetMenuAnchor:
                if (Enum.IsDefined((SmvmMenuAnchor)action.Index))
                {
                    _settings.SmvmMenuAnchor = (SmvmMenuAnchor)action.Index;
                    _settings.Save();
                }
                break;
            case SmvmActionType.SetMovementSpeed:
                _settings.SmvmMovementSpeed = action.Value;
                _settings.Save();
                break;
            case SmvmActionType.SetMouseSensitivity:
                _settings.SmvmMouseSensitivity = action.Value;
                _settings.Save();
                break;
            case SmvmActionType.SetSmoothing:
                _settings.SmvmMouseSmoothing = action.Value;
                _settings.Save();
                break;
            case SmvmActionType.ToggleInvertY:
                _settings.SmvmMouseInvertY = !_settings.SmvmMouseInvertY;
                _settings.Save();
                break;
            case SmvmActionType.ResetBindings:
                ResetSmvmBindings();
                _settings.Save();
                break;
            case SmvmActionType.ApplyMovieMakerDefaults:
                ApplyMovieMakerDefaults();
                _settings.Save();
                break;
            case SmvmActionType.ToggleMinimalPill:
                _settings.SmvmShowMinimalPill = !_settings.SmvmShowMinimalPill;
                _settings.Save();
                break;
            case SmvmActionType.ToggleHidePathWhilePlaying:
                _settings.SmvmHidePathWhilePlaying = !_settings.SmvmHidePathWhilePlaying;
                _settings.Save();
                break;
            case SmvmActionType.ToggleManualCamera:
                // Native input reserves the legacy action for Escape so the
                // protocol layout stays stable. F2 emits ReacquireCamera below.
                await ExitSmvmFreeCameraAsync().ConfigureAwait(true);
                break;
            case SmvmActionType.ReacquireCamera:
                await EnterSmvmFreeCameraAsync().ConfigureAwait(true);
                break;
            case SmvmActionType.CameraSelfTest:
                await _native.RunCameraSelfTestAsync(_stop.Token).ConfigureAwait(true);
                break;
            case SmvmActionType.CampathSelfTest:
                CampathPath? selfTestPath = null;
                if (!RunReplayEffect(() => selfTestPath = new CampathPath(
                        _campath.GetKeyframeSnapshot(),
                        _campath.InterpolationMode,
                        _campath.EasingMode)))
                    return;
                await _native.RunCampathSelfTestAsync(
                    selfTestPath!,
                    _stop.Token).ConfigureAwait(true);
                break;
        }
    }

    private async Task EnterSmvmFreeCameraAsync()
    {
        if (_native.CampathPlaying)
            throw new InvalidOperationException("Stop Campath before reacquiring SMVM Free Camera.");
        await _native.EnableManualCameraAsync(_stop.Token).ConfigureAwait(true);
    }

    private Task ExitSmvmFreeCameraAsync() =>
        _native.ExitFreeCameraAsync(_stop.Token);

    private async Task ObserveDeferredCameraExitAsync(Task exitTask)
    {
        try
        {
            await exitTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"SMVM deferred camera release failed: {ex.Message}");
        }
    }

    private void TraceCapture(
        SmvmCaptureStage stage,
        SmvmCaptureRejection rejection = SmvmCaptureRejection.None,
        string? detail = null)
    {
        if (!_captureDiagnosticsEnabled && stage != SmvmCaptureStage.CaptureRejected)
            return;
        var message = $"SMVM capture: stage={stage}";
        if (rejection != SmvmCaptureRejection.None)
            message += $", rejection={rejection}";
        if (!string.IsNullOrWhiteSpace(detail))
            message += $", {detail}";
        if (stage == SmvmCaptureStage.CaptureRejected)
            _log.Warn(message);
        else
            _log.Info(message);
    }

    private void ApplyDeadlockUiMode(
        DeadlockUiMode mode,
        ulong recoveryGeneration = 0,
        Func<bool>? stillCurrent = null)
    {
        lock (_processBoundaryGate)
        {
            lock (_presentationReplayGate)
            {
                var replayActive = IsReplayActive(_controller.State);
                var applied = stillCurrent is null
                    ? _deadlockUi.Apply(mode, replayActive, recoveryGeneration)
                    : _deadlockUi.ApplyIfCurrent(
                        mode,
                        replayActive,
                        stillCurrent,
                        recoveryGeneration);
                if (!applied)
                    return;

                // Clean Footage is deliberately transient. A fresh replay never starts
                // with both native UI stacks hidden; it returns to the user's normal or
                // custom replay UI preference.
                if ((mode is DeadlockUiMode.DeadlockUi or DeadlockUiMode.SmvmReplayUi) &&
                    _settings.SmvmDeadlockUiMode != mode)
                {
                    _settings.SmvmDeadlockUiMode = mode;
                    _settings.Save();
                }
            }
        }
    }

    private void ReassertDeadlockUiIfCurrent(Func<bool> stillCurrent)
    {
        lock (_processBoundaryGate)
        {
            lock (_presentationReplayGate)
            {
                if (stillCurrent())
                    _deadlockUi.ReassertSuppression(IsReplayActive(_controller.State));
            }
        }
    }

    private async Task ExecuteReplaySeekAsync(int tick)
    {
        Interlocked.Increment(ref _replaySeekInFlight);
        try
        {
            await _native.SeekReplayAsync(tick, _stop.Token).ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Decrement(ref _replaySeekInFlight);
        }
    }

    private bool TryExecutePlaybackSpeedAction(
        double speed,
        DemoPlaybackSpeedActionLease lease,
        SmvmQueuedActionLease queuedActionLease)
    {
        lock (_processBoundaryGate)
        {
            var replay = _controller.State;
            var currentReplayIdentity = IsReplayActive(replay)
                ? DemoStartupPolicy.CreateReplayIdentity(
                    replay.ReplayName!,
                    replay.TotalTicks,
                    replay.ReplaySessionGeneration)
                : null;
            var nativeLeaseCurrent = _native.IsConnectionLeaseCurrent(lease.NativeConnectionEpoch);
            if (!_demoPlaybackSpeed.IsOwnerActionCurrent(
                    lease,
                    Volatile.Read(ref _processBoundaryEpoch),
                    nativeLeaseCurrent ? lease.NativeConnectionEpoch : _native.ConnectionEpoch,
                    CurrentPresentationReplayEpoch,
                    currentReplayIdentity,
                    nativeLeaseCurrent))
            {
                _log.Info("Ignored a queued playback-speed action whose replay or process lease expired.");
                return false;
            }

            if (currentReplayIdentity is null ||
                !TryCaptureReplayCommandLease(currentReplayIdentity, out var commandLease))
            {
                _log.Info("Ignored replay-speed input because authoritative replay telemetry is not ready.");
                return false;
            }
            if (lease.ReplayIdentity is null &&
                queuedActionLease.SourceReplaySessionGeneration > 0 &&
                commandLease.ReplaySessionGeneration != queuedActionLease.SourceReplaySessionGeneration)
            {
                _log.Info("Ignored replay-speed input because the replay changed before it could be applied.");
                return false;
            }
            var applied = _controller.SetSpeedIfCurrent(speed, commandLease);
            if (applied)
            {
                _log.Info(
                    $"Replay speed applied: {speed * 100.0:0.##}% through demo_timescale " +
                    "(host_timescale remains neutral at 100%).");
            }
            else
            {
                _log.Info("Ignored replay-speed input because its VConsole replay lease expired.");
            }
            return applied;
        }
    }

    private void ReleaseFailedPlaybackSpeedIntent(DemoPlaybackSpeedActionLease lease)
    {
        if (!_demoPlaybackSpeed.ReleaseFailedOwnerIntent(lease) ||
            Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        BeginInvokeIsolated(
            () => _ = CoordinateDemoStartupAsync(_controller.State));
    }

    private DeadlockUiMode? ResolvePresentationTarget(SmvmAction action)
    {
        if (action.Type == SmvmActionType.SetDeadlockUiMode)
            return action.Tick == 0
                ? _deadlockUi.ProfileStatus.DesiredMode
                : (DeadlockUiMode)action.Index;
        if (action.Type == SmvmActionType.CycleReplayInterface)
        {
            var profile = _deadlockUi.ProfileStatus;
            var current = profile.DesiredMode == DeadlockUiMode.CleanFootage
                ? profile.DesiredPreviousVisibleMode
                : profile.DesiredMode;
            return current == DeadlockUiMode.DeadlockUi
                ? DeadlockUiMode.SmvmReplayUi
                : DeadlockUiMode.DeadlockUi;
        }
        if (action.Type == SmvmActionType.ToggleReplayPause)
            return _deadlockUi.ProfileStatus.DesiredMode;
        return null;
    }

    private async Task<bool> ConfirmPresentationConnectionLeaseAsync(
        long connectionEpoch,
        string operation)
    {
        if (await _native.ConfirmConnectionEpochAsync(connectionEpoch, _stop.Token)
                .ConfigureAwait(true))
            return true;

        if (!_stop.IsCancellationRequested)
            _log.Warn($"SMVM skipped or rolled back {operation}: native connection changed.");
        return false;
    }

    private async Task<bool> ApplyForwardPresentationWithLeaseAsync(
        ForwardPresentationLease lease,
        Func<Func<bool>, bool> apply,
        string operation,
        Func<bool>? additionalValidity = null)
    {
        var acquired = false;
        try
        {
            await _actionGate.WaitAsync(_stop.Token).ConfigureAwait(true);
            acquired = true;
            bool StillCurrent() =>
                IsForwardPresentationLeaseCurrent(lease) &&
                (additionalValidity?.Invoke() ?? true);
            if (!StillCurrent())
                return false;
            if (!await ConfirmPresentationConnectionLeaseAsync(lease.ConnectionEpoch, operation)
                    .ConfigureAwait(true))
                return false;
            if (!StillCurrent())
                return false;

            var applied = apply(StillCurrent);
            if (!applied)
                return false;

            if (!StillCurrent())
            {
                RestoreAfterInvalidForwardPresentationLease(lease);
                return false;
            }
            if (await ConfirmPresentationConnectionLeaseAsync(lease.ConnectionEpoch, operation)
                    .ConfigureAwait(true))
            {
                if (StillCurrent())
                    return true;
                RestoreAfterInvalidForwardPresentationLease(lease);
                return false;
            }

            _deadlockUi.RestoreAfterConnectionLeaseLoss();
            return false;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (acquired)
                _actionGate.Release();
        }
    }

    private async Task<bool> ReassertMovieUiWithConnectionLeaseAsync(
        ForwardPresentationLease lease,
        long? processBoundaryEpoch = null)
    {
        static bool IsForwardMode(DeadlockUiMode mode) =>
            mode is DeadlockUiMode.SmvmReplayUi or DeadlockUiMode.CleanFootage;

        if (processBoundaryEpoch is { } expectedEpoch &&
            !IsProcessBoundaryCurrent(expectedEpoch))
        {
            return false;
        }
        if (!IsForwardMode(_deadlockUi.ProfileStatus.DesiredMode))
            return true;
        return await ApplyForwardPresentationWithLeaseAsync(
            lease,
            stillCurrent => processBoundaryEpoch is { } processEpoch
                ? RunForProcessBoundary(
                    processEpoch,
                    () => ReassertSuppressionForPresentationLease(
                        replayActive: true,
                        () => stillCurrent() && IsProcessBoundaryCurrent(processEpoch)))
                : ReassertSuppressionForPresentationLease(
                    replayActive: true, stillCurrent),
            "transport recording-profile reassert",
            processBoundaryEpoch is { } epoch
                ? () => IsProcessBoundaryCurrent(epoch)
                : null).ConfigureAwait(true);
    }

    private bool ReassertSuppressionForPresentationLease(
        bool replayActive,
        Func<bool> stillCurrent)
    {
        var profile = _deadlockUi.ProfileStatus;
        return profile.DesiredMode == DeadlockUiMode.DeadlockUi
            ? profile.Ui.Mode == DeadlockUiMode.DeadlockUi && !profile.RestorePending
            : _deadlockUi.ApplyIfCurrent(
                profile.DesiredMode,
                replayActive,
                stillCurrent);
    }

    private bool IsPresentationIntentCurrent(long expectedEpoch) =>
        Volatile.Read(ref _presentationIntentEpoch) == expectedEpoch;

    private bool IsProcessBoundaryCurrent(long expectedEpoch) =>
        Volatile.Read(ref _processBoundaryEpoch) == expectedEpoch;

    private bool RunForProcessBoundary(long expectedEpoch, Func<bool> action)
    {
        lock (_processBoundaryGate)
            return IsProcessBoundaryCurrent(expectedEpoch) && action();
    }

    private bool IsAutomaticStartupLeaseCurrent(
        int generation,
        long processBoundaryEpoch,
        string replayIdentity,
        long presentationReplayEpoch)
    {
        if (!IsProcessBoundaryCurrent(processBoundaryEpoch) ||
            !_demoStartup.IsCurrent(generation) ||
            CurrentPresentationReplayEpoch != presentationReplayEpoch ||
            !IsReplayIdentityCurrent(replayIdentity))
        {
            return false;
        }

        var replay = _controller.State;
        return IsReplayActive(replay) && string.Equals(
            DemoStartupPolicy.CreateReplayIdentity(
                replay.ReplayName!,
                replay.TotalTicks,
                replay.ReplaySessionGeneration),
            replayIdentity,
            StringComparison.Ordinal);
    }

    private ForwardPresentationLease CaptureForwardPresentationLease() =>
        new(
            _native.ConnectionEpoch,
            Volatile.Read(ref _presentationIntentEpoch),
            CurrentPresentationReplayEpoch);

    private bool IsForwardPresentationLeaseCurrent(ForwardPresentationLease lease) =>
        IsForwardPresentationIntentAndReplayCurrent(lease) &&
        IsReplayActive(_controller.State) &&
        lease.ConnectionEpoch > 0 &&
        lease.ConnectionEpoch == _native.ConnectionEpoch &&
        _native.Connected;

    private bool IsForwardPresentationIntentAndReplayCurrent(ForwardPresentationLease lease) =>
        Volatile.Read(ref _stopping) == 0 &&
        IsPresentationIntentCurrent(lease.IntentEpoch) &&
        lease.ReplayEpoch == CurrentPresentationReplayEpoch;

    private void RestoreAfterInvalidForwardPresentationLease(ForwardPresentationLease lease)
    {
        // Native host retirement is a physical transport boundary, not an owner
        // request to show Deadlock UI. Preserve the movie-interface target so a
        // same-replay reconnect can reassert it. F9, replay replacement/end, and
        // shutdown invalidate the logical lease and deliberately clear it.
        if (IsForwardPresentationIntentAndReplayCurrent(lease) &&
            (lease.ConnectionEpoch != _native.ConnectionEpoch ||
             !_native.Connected ||
             !HasAuthoritativeReplayTelemetry(_controller.State)))
        {
            _deadlockUi.RestoreAfterConnectionLeaseLoss();
            return;
        }

        _deadlockUi.Restore(force: true);
    }

    private long CurrentPresentationReplayEpoch
    {
        get
        {
            lock (_presentationReplayGate)
                return _presentationReplayEpoch;
        }
    }

    private bool UpdatePresentationReplayEpoch(ReplayState replay)
    {
        // Disconnect and the marker-fenced reconnect snapshot are provisional,
        // not a replay transition. Retain the prior identity until Deadlock
        // reports an authoritative position again so same-demo recovery leases
        // cannot be invalidated by transport noise alone.
        if (!HasAuthoritativeReplayTelemetry(replay))
            return false;

        var identity = IsReplayActive(replay)
            ? DemoStartupPolicy.CreateReplayIdentity(
                replay.ReplayName!,
                replay.TotalTicks,
                replay.ReplaySessionGeneration)
            : string.Empty;
        lock (_presentationReplayGate)
        {
            // StateChanged can be raised concurrently by command readback and
            // VConsole output. Recheck while holding the presentation writer
            // lock: if B committed first, stale A is rejected; if B commits
            // after this check, B's synchronous callback waits here and then
            // becomes the final writer.
            if (!ReferenceEquals(replay, _controller.State))
                return false;
            if (string.Equals(identity, _presentationReplayIdentity, StringComparison.Ordinal))
                return false;
            _presentationReplayIdentity = identity;
            ++_presentationReplayEpoch;
            return !string.IsNullOrWhiteSpace(identity);
        }
    }

    private bool IsReplayIdentityCurrent(string expectedIdentity)
    {
        lock (_presentationReplayGate)
            return string.Equals(
                expectedIdentity,
                _presentationReplayIdentity,
                StringComparison.Ordinal);
    }

    private bool TryCaptureReplayCommandLease(
        string expectedReplayIdentity,
        out ReplayCommandLease lease)
    {
        var snapshot = _controller.CaptureReplayTelemetrySnapshot();
        var replay = snapshot.State;
        if (!HasAuthoritativeReplayTelemetry(replay) || !IsReplayActive(replay) ||
            !string.Equals(
                DemoStartupPolicy.CreateReplayIdentity(
                    replay.ReplayName!,
                    replay.TotalTicks,
                    replay.ReplaySessionGeneration),
                expectedReplayIdentity,
                StringComparison.Ordinal))
        {
            lease = default;
            return false;
        }

        lease = new ReplayCommandLease(
            snapshot.ConnectionGeneration,
            replay.ReplaySessionGeneration,
            replay.ReplayName!.Trim());
        return true;
    }

    private static ulong RecoveryGeneration(SmvmAction action) => action.Tick switch
    {
        > 0 => checked((ulong)action.Tick),
        < 0 when action.Type == SmvmActionType.SetDeadlockUiMode =>
            checked((ulong)-action.Tick),
        _ => 0,
    };

    private async Task ExecuteKeyframeSeekAsync()
    {
        Interlocked.Increment(ref _replaySeekInFlight);
        try
        {
            await _campath.GoToAsync().ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Decrement(ref _replaySeekInFlight);
        }
    }

    /// <summary>Publishes the explicit Load picker list (saved paths + recoverable draft).</summary>
    private async Task PublishDocumentsAsync()
    {
        try
        {
            var replay = _controller.State;
            CampathReplayIdentifier? identifier =
                replay.Connected && !string.IsNullOrWhiteSpace(replay.ReplayName) && replay.CurrentTick is not null
                    ? new CampathReplayIdentifier(replay.ReplayName, replay.TotalTicks)
                    : null;
            await _native.PublishCampathDocumentsAsync(
                _campath.GetDocuments(), identifier, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _log.Warn($"SMVM path-list synchronization deferred: {ex.Message}");
        }
    }

    private void ResetSmvmBindings()
    {
        ApplyBindingPreset([
            (111, "Tab"), (112, "Mouse3"), (113, "L"), (114, "F10"), (122, "F9"),
            (123, "F8"), (124, "F2"), (125, "N"),
            (100, "W"), (101, "S"), (102, "A"), (103, "D"), (104, "Space"),
            (105, "LeftCtrl"), (106, "LeftShift"), (107, "LeftAlt"),
            (108, "Q"), (109, "E"), (110, "R"),
            (115, "F3"), (116, "F5"), (117, "F4"), (118, "Ctrl+Z"), (119, "Ctrl+Y"),
            (120, string.Empty), (121, string.Empty), (126, string.Empty),
            (127, "PageUp"), (128, "PageDown")
        ], "Canonical defaults");
    }

    private void ApplyMovieMakerDefaults()
    {
        ApplyBindingPreset([
            (111, "Tab"), (112, "Mouse3"), (113, "L"), (114, "F10"), (122, "F9"),
            (123, "F8"), (124, "F2"), (125, "N"),
            (100, "W"), (101, "S"), (102, "A"), (103, "D"), (104, "Space"),
            (105, "LeftCtrl"), (106, "LeftShift"), (107, "LeftAlt"),
            (108, "Q"), (109, "E"), (110, "R"),
            (115, "F3"), (116, string.Empty), (117, "F4"),
            (118, "Ctrl+Z"), (119, "Ctrl+Y"),
            (120, string.Empty), (121, string.Empty), (126, string.Empty),
            (127, string.Empty), (128, string.Empty)
        ], "Movie Maker defaults");
    }

    private void ApplyBindingPreset((int Slot, string Binding)[] preset, string name)
    {
        var parsed = new List<(int Slot, InputBinding Binding)>();
        foreach (var item in preset)
        {
            if (string.IsNullOrEmpty(item.Binding))
                continue;
            if (!InputBinding.TryParse(item.Binding, out var binding) ||
                !SmvmInputCode.IsBindingAllowedForSlot(item.Slot, binding))
            {
                _log.Warn($"SMVM {name} rejected: invalid binding {item.Binding} for " +
                          $"{DescribeSmvmBinding(item.Slot)}.");
                return;
            }
            foreach (var other in parsed)
            {
                if (!BindingsConflict(other.Binding, binding))
                    continue;
                _log.Warn($"SMVM {name} rejected: {item.Binding} conflicts with " +
                          $"{DescribeSmvmBinding(other.Slot)}.");
                return;
            }
            parsed.Add((item.Slot, binding));
        }

        foreach (var item in preset)
            AssignSmvmBinding(item.Slot, item.Binding);
        _log.Info($"SMVM {name} applied after conflict check ({parsed.Count} active bindings).");
    }

    private static bool BindingsConflict(InputBinding left, InputBinding right)
    {
        if (left.Kind != right.Kind || left.Modifiers != right.Modifiers)
            return false;
        if (left.Code == right.Code)
            return true;
        if (left.Kind != InputBindingKind.Keyboard)
            return false;
        return IsModifierFamily(left.Code, right.Code, 0x10, 0xA0, 0xA1) ||
               IsModifierFamily(left.Code, right.Code, 0x11, 0xA2, 0xA3) ||
               IsModifierFamily(left.Code, right.Code, 0x12, 0xA4, 0xA5);
    }

    private static bool IsModifierFamily(uint left, uint right, uint generic, uint leftKey, uint rightKey) =>
        (left == generic && (right == leftKey || right == rightKey)) ||
        (right == generic && (left == leftKey || left == rightKey));

    private void SetSmvmBinding(int index, double rawValue)
    {
        if (index is < 100 or > 128)
        {
            _log.Warn($"SMVM binding rejected: unsupported slot {index}.");
            return;
        }

        if (rawValue == 0)
        {
            AssignSmvmBinding(index, string.Empty);
            _settings.Save();
            _log.Info($"SMVM binding cleared: {DescribeSmvmBinding(index)}.");
            return;
        }

        if (!double.IsFinite(rawValue) || rawValue < 0 || rawValue > uint.MaxValue ||
            rawValue != Math.Truncate(rawValue) ||
            !SmvmInputCode.TryDecode((uint)rawValue, out var binding))
        {
            _log.Warn($"SMVM binding rejected: invalid input for {DescribeSmvmBinding(index)}.");
            return;
        }
        if (!SmvmInputCode.IsBindingAllowedForSlot(index, binding))
        {
            _log.Warn($"SMVM binding rejected: {DescribeSmvmBinding(index)} requires a keyboard key.");
            return;
        }

        var canonical = binding.ToString();
        foreach (var otherIndex in Enumerable.Range(100, 29))
        {
            if (otherIndex == index ||
                !InputBinding.TryParse(ReadSmvmBinding(otherIndex), out var existing) ||
                !BindingsConflict(existing, binding))
                continue;

            _log.Warn($"SMVM binding rejected: {canonical} is assigned to {DescribeSmvmBinding(otherIndex)}.");
            return;
        }

        AssignSmvmBinding(index, canonical);
        _settings.Save();
        _log.Info($"SMVM binding set: {DescribeSmvmBinding(index)} = {canonical}.");
    }

    private string ReadSmvmBinding(int index) => index switch
    {
        100 => _settings.SmvmForwardHotkey,
        101 => _settings.SmvmBackHotkey,
        102 => _settings.SmvmLeftHotkey,
        103 => _settings.SmvmRightHotkey,
        104 => _settings.SmvmUpHotkey,
        105 => _settings.SmvmDownHotkey,
        106 => _settings.SmvmFastHotkey,
        107 => _settings.SmvmPrecisionHotkey,
        108 => _settings.SmvmRollLeftHotkey,
        109 => _settings.SmvmRollRightHotkey,
        110 => _settings.SmvmRollResetHotkey,
        111 => _settings.SmvmMenuHotkey,
        112 => _settings.SmvmAddHotkey,
        113 => _settings.SmvmDeleteHotkey,
        114 => _settings.SmvmCleanViewHotkey,
        115 => _settings.SmvmPlayStartHotkey,
        116 => _settings.SmvmPlayCurrentHotkey,
        117 => _settings.SmvmStopHotkey,
        118 => _settings.SmvmUndoHotkey,
        119 => _settings.SmvmRedoHotkey,
        120 => _settings.SmvmShowPathHotkey,
        121 => _settings.SmvmShowCamerasHotkey,
        122 => _settings.SmvmRestoreUiHotkey,
        123 => _settings.SmvmCycleUiHotkey,
        124 => _settings.SmvmToggleFreeCameraHotkey,
        125 => _settings.SmvmReplayPauseHotkey,
        126 => _settings.SmvmShowLabelsHotkey,
        127 => _settings.SmvmStepBackHotkey,
        128 => _settings.SmvmStepForwardHotkey,
        _ => string.Empty,
    };

    private void AssignSmvmBinding(int index, string value)
    {
        switch (index)
        {
            case 100: _settings.SmvmForwardHotkey = value; break;
            case 101: _settings.SmvmBackHotkey = value; break;
            case 102: _settings.SmvmLeftHotkey = value; break;
            case 103: _settings.SmvmRightHotkey = value; break;
            case 104: _settings.SmvmUpHotkey = value; break;
            case 105: _settings.SmvmDownHotkey = value; break;
            case 106: _settings.SmvmFastHotkey = value; break;
            case 107: _settings.SmvmPrecisionHotkey = value; break;
            case 108: _settings.SmvmRollLeftHotkey = value; break;
            case 109: _settings.SmvmRollRightHotkey = value; break;
            case 110: _settings.SmvmRollResetHotkey = value; break;
            case 111: _settings.SmvmMenuHotkey = value; break;
            case 112: _settings.SmvmAddHotkey = value; break;
            case 113: _settings.SmvmDeleteHotkey = value; break;
            case 114: _settings.SmvmCleanViewHotkey = value; break;
            case 115: _settings.SmvmPlayStartHotkey = value; break;
            case 116: _settings.SmvmPlayCurrentHotkey = value; break;
            case 117: _settings.SmvmStopHotkey = value; break;
            case 118: _settings.SmvmUndoHotkey = value; break;
            case 119: _settings.SmvmRedoHotkey = value; break;
            case 120: _settings.SmvmShowPathHotkey = value; break;
            case 121: _settings.SmvmShowCamerasHotkey = value; break;
            case 122: _settings.SmvmRestoreUiHotkey = value; break;
            case 123: _settings.SmvmCycleUiHotkey = value; break;
            case 124: _settings.SmvmToggleFreeCameraHotkey = value; break;
            case 125: _settings.SmvmReplayPauseHotkey = value; break;
            case 126: _settings.SmvmShowLabelsHotkey = value; break;
            case 127: _settings.SmvmStepBackHotkey = value; break;
            case 128: _settings.SmvmStepForwardHotkey = value; break;
        }
    }

    private static string DescribeSmvmBinding(int index) => index switch
    {
        100 => "Forward",
        101 => "Back",
        102 => "Left",
        103 => "Right",
        104 => "Up",
        105 => "Down",
        106 => "Fast",
        107 => "Precision",
        108 => "Roll Left",
        109 => "Roll Right",
        110 => "Roll Reset",
        111 => "Menu",
        112 => "Add Keyframe",
        113 => "Delete Keyframe",
        114 => "Clean Footage",
        115 => "Play From Start",
        116 => "Play From Current",
        117 => "Stop",
        118 => "Undo",
        119 => "Redo",
        120 => "Show Path",
        121 => "Show Cameras",
        122 => "Emergency Exit + Restore",
        123 => "Cycle Replay Interface",
        124 => "Toggle Free Camera",
        125 => "Replay Play/Pause",
        126 => "Show Labels",
        127 => "Replay Step Back",
        128 => "Replay Step Forward",
        _ => $"Slot {index}",
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;
        _native.SmvmActionReceived -= OnActionReceived;
        _native.StatusChanged -= OnNativeStatusChanged;
        _native.SmvmSnapshotProvider = null;
        _campath.EditorStateChanged -= OnEditorStateChanged;
        _native.CampathStateChanged -= OnCampathStateChanged;
        _controller.StateChanged -= OnReplayStateChanged;
        // Invalidate every captured forward lease before cancellation/drain.
        // The final inverse must be the last presentation transaction, not the
        // middle of a forward operation that was already waiting on the gate.
        Interlocked.Increment(ref _presentationIntentEpoch);
        _stop.Cancel();
        await _actionGate.WaitAsync().ConfigureAwait(false);
        _actionGate.Release();
        await _pathPublishGate.WaitAsync().ConfigureAwait(false);
        _pathPublishGate.Release();
        _deadlockUi.Restore(force: true);
        _actionGate.Dispose();
        _pathPublishGate.Dispose();
        _stop.Dispose();
    }
}
