using System.Windows.Threading;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Globalization;
using Microsoft.Win32;
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
    private readonly MovieRecordingController _movieRecording;
    private readonly MovieVisualController _movieVisual;
    private string? _lookPresetPath;
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
    private int _compositingTransitionInFlight;
    private int _compositingCancelled;
    private long? _compositingChromaStopTick;

    public SmvmHostCoordinator(
        ICameraService camera,
        ReplayController controller,
        NativeReplayCameraSession native,
        CampathViewModel campath,
        IAppSettings settings,
        ILogService log,
        Dispatcher dispatcher,
        Func<string?>? deadlockExecutablePath = null)
    {
        _camera = camera;
        _controller = controller;
        _native = native;
        _campath = campath;
        _settings = settings;
        _log = log;
        _deadlockUi = new DeadlockUiController(controller, log);
        _movieRecording = new MovieRecordingController(
            controller,
            log,
            () => _settings.SmvmMovieCaptureRoot,
            deadlockExecutablePath ?? (() => _settings.DeadlockPath));
        _ = _movieRecording.ApplyPreset(_settings.SmvmMovieRecordingPreset);
        if (_movieRecording.State.CaptureFps != _settings.SmvmMovieCaptureFps)
            _ = _movieRecording.SetCaptureFps(_settings.SmvmMovieCaptureFps);
        if (_movieRecording.State.OutputMode != _settings.SmvmMovieOutputMode)
            _ = _movieRecording.SetOutputMode(_settings.SmvmMovieOutputMode);
        if (_movieRecording.State.OutputResolution != _settings.SmvmMovieOutputResolution)
            _ = _movieRecording.SetOutputResolution(_settings.SmvmMovieOutputResolution);
        var recordingPasses = new[]
        {
            MovieCapturePass.Beauty,
            MovieCapturePass.WorldDepthPfm,
            MovieCapturePass.WorldDepthAvi,
            MovieCapturePass.GreenscreenFreeCamera,
        };
        var desiredRecordingPasses = _settings.SmvmMovieCapturePasses;
        const MovieCapturePass legacyCompositingPasses =
            MovieCapturePass.Beauty |
            MovieCapturePass.WorldDepthPfm |
            MovieCapturePass.WorldDepthAvi;
        if (_settings.SmvmMovieRecordingPreset == MovieRecordingPreset.Compositing ||
            desiredRecordingPasses == legacyCompositingPasses)
        {
            desiredRecordingPasses |= MovieCapturePass.GreenscreenFreeCamera;
        }
        // Enable desired passes first so a valid depth-only recipe can then
        // remove the default Beauty pass without tripping the non-zero guard.
        foreach (var pass in recordingPasses.Where(pass =>
                     (desiredRecordingPasses & pass) != 0))
        {
            if ((_movieRecording.State.Passes & pass) == 0)
                _ = _movieRecording.SetPass(pass, enabled: true);
        }
        foreach (var pass in recordingPasses.Where(pass =>
                     (desiredRecordingPasses & pass) == 0))
        {
            if ((_movieRecording.State.Passes & pass) != 0)
                _ = _movieRecording.SetPass(pass, enabled: false);
        }
        // Persist the repaired invariant as well as enforcing it in memory, so
        // an older zero-pass settings file cannot keep presenting as Custom / 0.
        _settings.SmvmMovieCapturePasses = _movieRecording.State.Passes;
        _settings.SmvmMovieOutputMode = _movieRecording.State.OutputMode;
        _settings.SmvmMovieRecordingPreset = _movieRecording.State.Preset;
        _settings.Save();
        var requestedPhysicalOptions =
            (_settings.SmvmMovieDisablePostProcessing
                ? MovieRecordingOptions.DisablePostProcessing
                : MovieRecordingOptions.None) |
            (_settings.SmvmMovieMuteDialogue
                ? MovieRecordingOptions.MuteDialogue
                : MovieRecordingOptions.None);
        _ = _movieRecording.Apply(requestedPhysicalOptions);
        _movieVisual = new MovieVisualController(controller, log);
        var configuredFog = _settings.SmvmCustomFog;
        if (_settings.SmvmCustomFogEnabled && !configuredFog.IsPractical)
        {
            configuredFog = FogConfiguration.Default;
            _settings.SmvmCustomFog = configuredFog;
            _settings.Save();
            _log.Info("Custom fog values were reset to the visible fog preset.");
        }
        var configuredGreenscreen =
            _movieRecording.State.Preset == MovieRecordingPreset.Greenscreen
                ? GreenscreenMode.FreeCamera
                : GreenscreenMode.Off;
        if (configuredGreenscreen != _settings.SmvmGreenscreenMode)
        {
            _settings.SmvmGreenscreenMode = configuredGreenscreen;
            _settings.Save();
        }
        _movieVisual.LoadConfiguration(
            _settings.SmvmRuleOfThirds,
            _settings.SmvmCustomFogEnabled,
            configuredFog,
            configuredGreenscreen);
        _controller.OutputSilenceExpected = () =>
        {
            var recording = _movieRecording.State;
            return recording.IsRecording || recording.IsFinalizing ||
                   recording.CompositingStage != MovieCompositingStage.None;
        };
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
        _camera.CapabilitiesChanged += OnCameraCapabilitiesChanged;
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
            if (SmvmQueuedActionLeasePolicy.BypassesSerializedCameraActionGate(action))
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
                _log.Info(
                    $"Replay speed input received: {action.Value * 100.0:0.##}%; " +
                    "dispatching independently from camera and tick-update work.");
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
                _movieRecording.Stop();
                _movieVisual.RestorePhysicalVisuals();
                _deadlockUi.Restore(force: true, RecoveryGeneration(action));
                _settings.SmvmDeadlockUiMode = DeadlockUiMode.DeadlockUi;
                _settings.SmvmCustomFogEnabled = false;
                _settings.SmvmGreenscreenMode = GreenscreenMode.Off;
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
                _movieRecording.AbandonForProcessBoundary();
                _movieVisual.AbandonForProcessBoundary();
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

    private void OnCameraCapabilitiesChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _stopping) != 0)
            return;
        BeginInvokeIsolated(() => _ = CoordinateDemoStartupAsync(_controller.State));
    }

    private async Task CoordinateAfterNativeStatusAsync(bool connectionBecameLive)
    {
        var processBoundaryEpoch = Volatile.Read(ref _processBoundaryEpoch);
        if (connectionBecameLive && IsPlaybackSceneReady() &&
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
        BeginInvokeIsolated(() => _ = HandleCampathStateChangedAsync(status));

    private async Task HandleCampathStateChangedAsync(CampathPlaybackStatus status)
    {
        if (!status.IsTerminal)
        {
            RefreshEditorSnapshot();
            return;
        }

        var recording = _movieRecording.State;
        var stage = recording.CompositingStage;
        var groupDirectory = recording.CaptureGroupDirectory;
        var selectedPasses = recording.Passes;
        // A completed status from the first replay can still be queued while
        // the second replay is already live. Never let that stale terminal
        // notification stop the newly-owned Chroma campath.
        if (CampathTelemetryLeasePolicy.ShouldIgnoreTerminalDuringCompositing(
                status,
                stage,
                _native.CampathPlaying))
        {
            RefreshEditorSnapshot();
            return;
        }
        _movieRecording.StopCinematicRecording();
        RefreshEditorSnapshot();
        if (stage == MovieCompositingStage.None)
            return;
        if (Interlocked.Exchange(ref _compositingTransitionInFlight, 1) != 0)
            return;

        try
        {
            await _movieRecording.WaitForFinalizationAsync().ConfigureAwait(true);
            var finalization = _movieRecording.State;
            var completedNormally = status.State == CampathPlaybackState.Completed &&
                                    Volatile.Read(ref _compositingCancelled) == 0;
            var visualWorldPassesSurvived = stage == MovieCompositingStage.World &&
                                            WorldVisualPassesComplete(
                                                groupDirectory,
                                                selectedPasses);
            if (!completedNormally ||
                (finalization.Error != MovieRecordingError.None && !visualWorldPassesSurvived))
            {
                RestoreGreenscreenAfterCompositing();
                var reason = finalization.Error != MovieRecordingError.None
                    ? finalization.Detail
                    : $"Synchronized take stopped during {stage}: {status.Detail}";
                _movieRecording.CompleteCompositingBatch(reason, succeeded: false);
                return;
            }

            if (stage == MovieCompositingStage.World)
            {
                var worldManifestPath = Path.Combine(
                    groupDirectory,
                    "world",
                    "deadlockmvm_capture.txt");
                var worldManifest = File.Exists(worldManifestPath)
                    ? File.ReadAllLines(worldManifestPath)
                    : Array.Empty<string>();
                var worldFrameCount = ReadCaptureMetric(worldManifest, "Frames observed:");
                var worldLastTick = ReadCaptureMetric(worldManifest, "Last replay tick:");
                if (worldFrameCount is not > 0 ||
                    !_movieRecording.ArmCompositingChroma(worldFrameCount.Value) ||
                    !_movieVisual.SetGreenscreenMode(GreenscreenMode.FreeCamera))
                {
                    RestoreGreenscreenAfterCompositing();
                    _movieRecording.CompleteCompositingBatch(
                        "World was preserved, but the Chroma replay could not be armed.",
                        succeeded: false);
                    return;
                }
                // Bound the Chroma take by the World tick span as well as its
                // frame count: the second play can run at a different cadence,
                // and the frame cap alone allowed it to stop short of the
                // World's last tick.
                _compositingChromaStopTick = worldLastTick is >= 0 ? worldLastTick : null;
                await EnterSmvmFreeCameraAsync().ConfigureAwait(true);
                await _campath.PlayAsync(
                        CampathPlayMode.FromStart,
                        StartArmedCinematicWithMovieVisuals)
                    .ConfigureAwait(true);
                if (!_native.CampathPlaying)
                {
                    _movieRecording.StopCinematicRecording();
                    await _movieRecording.WaitForFinalizationAsync().ConfigureAwait(true);
                    RestoreGreenscreenAfterCompositing();
                    var reportComplete = WriteCompositingTakeReport(
                        groupDirectory,
                        selectedPasses,
                        out var reportDetail);
                    _movieRecording.CompleteCompositingBatch(
                        reportDetail,
                        reportComplete);
                }
                return;
            }

            RestoreGreenscreenAfterCompositing();
            var aligned = WriteCompositingTakeReport(
                groupDirectory,
                selectedPasses,
                out var detail);
            _movieRecording.CompleteCompositingBatch(detail, aligned);
        }
        catch (Exception ex)
        {
            _movieRecording.Stop();
            await _movieRecording.WaitForFinalizationAsync().ConfigureAwait(true);
            RestoreGreenscreenAfterCompositing();
            _movieRecording.CompleteCompositingBatch(
                $"Synchronized take transition failed: {ex.Message}",
                succeeded: false);
            _log.Warn($"Synchronized compositing take failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _compositingTransitionInFlight, 0);
            RefreshEditorSnapshot();
        }
    }

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
            var recording = _movieRecording.State;
            if (_compositingChromaStopTick is { } stopTick &&
                recording.CompositingStage == MovieCompositingStage.Chroma &&
                recording.IsRecording &&
                current.CurrentTick is { } currentTick &&
                currentTick >= stopTick)
            {
                // The Chroma replay can advance at a different cadence than the
                // World pass. Stop it at the World's last tick so both AVI
                // layers cover the identical replay range instead of trusting
                // the frame count alone.
                _compositingChromaStopTick = null;
                _movieRecording.StopCinematicRecording();
            }
            if (replayEpochChanged)
            {
                _movieRecording.Stop();
                _movieVisual.AbandonForProcessBoundary();
            }
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
                !HasAuthoritativeReplayTelemetry(replay) ||
                !IsPlaybackSceneReady())
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
                _ = ReassertRuntimeSuppressionWithConnectionLeaseAsync(
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
        if (_native.Connected && IsPlaybackSceneReady() &&
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
        var playbackSceneReady = replayActive && IsPlaybackSceneReady();
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
        if (replayTelemetryAuthoritative && replayActive && playbackSceneReady &&
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
                    playbackSceneReady),
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
                _movieRecording.Stop();
                _movieVisual.SuspendPhysicalVisuals();
                var profile = _deadlockUi.ProfileStatus;
                if (profile.RequiresRestore || profile.DesiredMode != DeadlockUiMode.DeadlockUi)
                    _deadlockUi.Restore();
            }
            return;
        }

        if (!playbackSceneReady)
            return;

        // Fog and greenscreen are persistent movie-tool choices. Reassert
        // them exactly once after a fresh process/replay scene becomes usable;
        // configuration itself remains side-effect-free while no replay exists.
        if (!_movieVisual.ReassertConfiguredVisuals())
            return;

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
                    // The previous internal build wrote demo_timescale, but the
                    // physical tick trace proved that Deadlock ignored it. Keep
                    // that ineffective multiplier neutral and make the engine's
                    // working host clock the single playback-speed authority.
                    _controller.SendRaw(ReplayCommands.ResetLegacyDemoTimescale);
                    if (!_controller.SetSpeedIfCurrent(1.0, speedCommandLease))
                        throw new InvalidOperationException(
                            "Replay changed while startup playback speed was being normalized.");
                    _log.Info(
                        "Demo startup: legacy demo scaling neutralized and host playback speed reset to 100%.");
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

    private bool IsPlaybackSceneReady() =>
        _camera is CompositeCameraService { NativeStatus.Attached: true };

    private EditorSnapshot ReadEditorSnapshot()
    {
        var keys = _campath.GetKeyframeSnapshot();
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
        var movieRecording = _movieRecording.State;
        var movieVisual = _movieVisual.State;
        var deadlockUi = presentation.Ui;
        var cameraTransactionActive = CampathTelemetryLeasePolicy.KeepsCameraTransactionActive(
            replayActive,
            _native.CampathPlaying,
            native?.Flags.HasFlag(InProcessStatusFlags.CampathActive) == true);
        var ownership = ResolveCameraOwnership(cameraTransactionActive, native);
        var availability = ResolveCameraAvailability(
            internalEnabled, replayActive, replay, native, playback, ownership);
        var capabilities = ResolveCapabilities(internalEnabled, native);
        var cameraOwnershipIntent = cameraTransactionActive && _native.CameraOwned;
        var cameraReadable = cameraTransactionActive && _native.Connected &&
            (native?.CameraObserved == true ||
             (native?.Camera.IsValid == true &&
              ownership is CameraOwnership.SmvmManualCamera or
                  CameraOwnership.SmvmRestore or CameraOwnership.SmvmCampath));
        var rollWritable = availability == CameraAvailability.Ready &&
                           ownership == CameraOwnership.SmvmManualCamera &&
                           _native.ManualCameraEstablished;

        var flags = SmvmSnapshotFlags.None;
        if (cameraTransactionActive) flags |= SmvmSnapshotFlags.ReplayActive;
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
                : !string.IsNullOrWhiteSpace(movieRecording.Detail) ? movieRecording.Detail
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
            replay.ReplaySessionGeneration,
            (MovieRecordingFlags)(uint)movieRecording.EnabledOptions |
                (movieRecording.WorldDepthPassEnabled ? MovieRecordingFlags.WorldDepth : MovieRecordingFlags.None) |
                (movieRecording.IsRecording ? MovieRecordingFlags.Active : MovieRecordingFlags.None) |
                (movieRecording.IsArmed ? MovieRecordingFlags.Armed : MovieRecordingFlags.None) |
                (movieRecording.IsFinalizing ? MovieRecordingFlags.Finalizing : MovieRecordingFlags.None),
            movieRecording.IsRecording || movieRecording.IsArmed || movieRecording.IsFinalizing
                ? movieRecording.NativeCaptureName
                : string.Empty,
            movieRecording.CaptureFps,
            movieRecording.Preset,
            movieRecording.OutputMode,
            movieRecording.Passes,
            (movieVisual.RuleOfThirds ? MovieToolFlags.RuleOfThirds : MovieToolFlags.None) |
                (movieVisual.FogEnabled ? MovieToolFlags.CustomFog : MovieToolFlags.None),
            movieVisual.Greenscreen,
            movieVisual.Fog,
            _settings.SmvmGreenscreenColorRgb,
            (movieRecording.IsRecording || movieRecording.IsArmed || movieRecording.IsFinalizing) &&
                !string.IsNullOrWhiteSpace(movieRecording.CaptureDirectory)
                ? movieRecording.CaptureDirectory
                : _movieRecording.CaptureRoot,
            movieRecording.OutputResolution,
            movieRecording.ActivePasses,
            movieRecording.CompositingStage,
            movieRecording.CaptureAudio,
            checked((ulong)Math.Max(0, movieRecording.ExpectedFrameCount)),
            _settings.SmvmLook,
            SmvmInputCode.ParseForSlotOrDefault(129, _settings.SmvmEffectsHotkey, "CapsLock"),
            SmvmInputCode.ParseForSlotOrDefault(130, _settings.SmvmCinematicStartHotkey, "Space"),
            SmvmInputCode.ParseForSlotOrDefault(131, _settings.SmvmPlaybackSlowerHotkey, "Left"),
            SmvmInputCode.ParseForSlotOrDefault(132, _settings.SmvmPlaybackFasterHotkey, "Right"),
            SmvmInputCode.ParseForSlotOrDefault(133, _settings.SmvmCancelHotkey, "Escape"),
            SmvmInputCode.ParseForSlotOrDefault(134, _settings.SmvmCameraSlowerHotkey, "OemMinus"),
            SmvmInputCode.ParseForSlotOrDefault(135, _settings.SmvmCameraFasterHotkey, "OemPlus"));
    }

    private async Task ExecuteLookActionAsync(SmvmAction action, Func<bool> actionStillCurrent)
    {
        bool CanApplyLook()
        {
            var recording = _movieRecording.State;
            return actionStillCurrent() &&
                !recording.IsArmed && !recording.IsRecording && !recording.IsFinalizing &&
                recording.CompositingStage == MovieCompositingStage.None;
        }

        if (!CanApplyLook())
            return;

        var previous = _settings.SmvmLook;
        var next = previous;
        var nextPresetPath = _lookPresetPath;
        switch (action.Type)
        {
            case SmvmActionType.SetLookEnabled:
                if (action.Value is not 0 and not 1)
                    return;
                next = previous with { Enabled = action.Value == 1, Modified = true };
                break;
            case SmvmActionType.SetLookValue:
                next = previous.SetValue(action.Index, action.Value);
                break;
            case SmvmActionType.ResetLook:
                next = new LookSettings();
                nextPresetPath = null;
                break;
            case SmvmActionType.SelectLookPreset:
                next = LookPresetService.Factory(action.Index);
                nextPresetPath = null;
                break;
            case SmvmActionType.ImportLookPreset:
            case SmvmActionType.LoadLookLut:
                var open = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = action.Type == SmvmActionType.LoadLookLut
                        ? "3D CUBE LUT (*.cube)|*.cube"
                        : "Reshade preset (*.json)|*.json",
                    InitialDirectory = Path.Combine(DeadlockMVM.Core.AppPaths.DataDirectory, "looks"),
                };
                if (open.ShowDialog() != true || !CanApplyLook())
                    return;
                next = await Task.Run(() => action.Type == SmvmActionType.LoadLookLut
                    ? LookPresetService.LoadCube(open.FileName, previous)
                    : LookPresetService.Read(open.FileName));
                if (action.Type == SmvmActionType.ImportLookPreset)
                    nextPresetPath = open.FileName;
                break;
            case SmvmActionType.SaveLookPreset:
            case SmvmActionType.ExportLookPreset:
                if (action.Type == SmvmActionType.SaveLookPreset && action.Index == 2)
                {
                    next = previous with { PresetName = action.Text.Trim(), Modified = true };
                    break;
                }
                var destination = action.Type == SmvmActionType.SaveLookPreset && action.Index == 0
                    ? nextPresetPath
                    : null;
                if (destination is null)
                {
                    var save = new Microsoft.Win32.SaveFileDialog
                    {
                        Filter = "Reshade preset (*.json)|*.json",
                        FileName = previous.PresetName + " copy.json",
                        InitialDirectory = Path.Combine(DeadlockMVM.Core.AppPaths.DataDirectory, "looks"),
                    };
                    if (save.ShowDialog() != true)
                        return;
                    destination = save.FileName;
                    next = previous with { PresetName = Path.GetFileNameWithoutExtension(destination) };
                }
                // A modal dialog can pump replay/take transitions. Revalidate before disk writes.
                if (!CanApplyLook())
                    return;
                next = next with { Modified = false };
                await Task.Run(() => LookPresetService.Save(destination, next));
                if (action.Type == SmvmActionType.SaveLookPreset)
                    nextPresetPath = destination;
                break;
        }

        // Publish settings and their save destination together after asynchronous work.
        if (!CanApplyLook())
            return;
        if (!next.IsValid)
            throw new InvalidDataException("Invalid Reshade preset.");

        var revision = checked(previous.Revision + 1);
        var lutRevision = next.LutSize == 0
            ? 0
            : ReferenceEquals(next.LutRgb, previous.LutRgb) && previous.LutRevision != 0
                ? previous.LutRevision
                : revision;
        _settings.SmvmLook = next with { Revision = revision, LutRevision = lutRevision };
        _lookPresetPath = nextPresetPath;
        _settings.Save();
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
        bool QueuedActionStillCurrent() =>
            IsQueuedActionLeaseCurrent(action, queuedActionLease);
        try
        {
            // Playback speed is an engine-clock command, not a camera action.
            // Do not strand it behind a protected seek, camera reacquisition,
            // or Campath preparation holding the serialized camera gate.
            if (SmvmQueuedActionLeasePolicy.BypassesSerializedCameraActionGate(action))
            {
                if (!QueuedActionStillCurrent())
                {
                    _log.Info(
                        $"Ignored queued SMVM action {action.Type}: its process, native, or replay lease expired.");
                    ReleaseFailedPlaybackSpeedIntent(playbackSpeedActionLease);
                    return;
                }
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

            await _actionGate.WaitAsync(_stop.Token).ConfigureAwait(true);
            acquired = true;
            if (!QueuedActionStillCurrent())
            {
                _log.Info($"Ignored queued SMVM action {action.Type}: its process, native, or replay lease expired.");
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
                _log.Info($"SMVM presentation request: target={(DeadlockUiMode)action.Index}, generation={RecoveryGeneration(action)}.");
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
            case SmvmActionType.SetMovieRecordingOption:
                if (Enum.IsDefined((MovieRecordingOption)action.Index) &&
                    action.Value is 0.0 or 1.0)
                {
                    var option = (MovieRecordingOptions)(1u << action.Index);
                    var enableOption = action.Value == 1.0;
                    if (_movieRecording.SetOption(option, enableOption))
                        SaveMovieRecordingSettings();
                }
                break;
            case SmvmActionType.SetMovieRecordingFps:
                if (action.Value == Math.Truncate(action.Value) &&
                    _movieRecording.SetCaptureFps(checked((int)action.Value)))
                    SaveMovieRecordingSettings();
                break;
            case SmvmActionType.SetMovieRecordingPreset:
                if (Enum.IsDefined((MovieRecordingPreset)action.Index) &&
                    _movieRecording.ApplyPreset((MovieRecordingPreset)action.Index))
                {
                    var wantsGreenscreen =
                        (_movieRecording.State.Passes & MovieCapturePass.GreenscreenFreeCamera) != 0 &&
                        (_movieRecording.State.Passes & MovieCapturePass.Beauty) == 0;
                    var greenscreenMode = wantsGreenscreen
                        ? GreenscreenMode.FreeCamera
                        : GreenscreenMode.Off;
                    if (_movieVisual.State.Greenscreen != greenscreenMode &&
                        _movieVisual.SetGreenscreenMode(greenscreenMode))
                    {
                        _settings.SmvmGreenscreenMode = greenscreenMode;
                    }
                    SaveMovieRecordingSettings();
                }
                break;
            case SmvmActionType.SetMovieOutputMode:
                if (Enum.IsDefined((MovieOutputMode)action.Index) &&
                    _movieRecording.SetOutputMode((MovieOutputMode)action.Index))
                    SaveMovieRecordingSettings();
                break;
            case SmvmActionType.SetMovieOutputResolution:
                if (Enum.IsDefined((MovieOutputResolution)action.Index) &&
                    _movieRecording.SetOutputResolution((MovieOutputResolution)action.Index))
                    SaveMovieRecordingSettings();
                break;
            case SmvmActionType.SetMovieCapturePass:
                if (action.Index is >= 0 and <= 3 && (action.Value is 0.0 or 1.0) &&
                    _movieRecording.SetPass(
                        (MovieCapturePass)(1u << action.Index),
                        action.Value == 1.0))
                {
                    var changedPass = (MovieCapturePass)(1u << action.Index);
                    if (changedPass is MovieCapturePass.Beauty or
                        MovieCapturePass.GreenscreenFreeCamera)
                    {
                        var greenscreenMode =
                            (_movieRecording.State.Passes &
                             MovieCapturePass.GreenscreenFreeCamera) != 0 &&
                            (_movieRecording.State.Passes & MovieCapturePass.Beauty) == 0
                                ? GreenscreenMode.FreeCamera
                                : GreenscreenMode.Off;
                        if (_movieVisual.State.Greenscreen == greenscreenMode ||
                            _movieVisual.SetGreenscreenMode(greenscreenMode))
                        {
                            _settings.SmvmGreenscreenMode = greenscreenMode;
                        }
                    }
                    SaveMovieRecordingSettings();
                }
                break;
            case SmvmActionType.OpenMovieCaptureFolder:
                OpenMovieCaptureFolder();
                break;
            case SmvmActionType.ChooseMovieCaptureFolder:
                ChooseMovieCaptureFolder();
                break;
            case SmvmActionType.SetRuleOfThirds:
                if (action.Value is 0.0 or 1.0 &&
                    _movieVisual.SetRuleOfThirds(action.Value == 1.0))
                {
                    _settings.SmvmRuleOfThirds = action.Value == 1.0;
                    _settings.Save();
                }
                break;
            case SmvmActionType.SetLookEnabled:
            case SmvmActionType.SetLookValue:
            case SmvmActionType.ResetLook:
            case SmvmActionType.SelectLookPreset:
            case SmvmActionType.SaveLookPreset:
            case SmvmActionType.ImportLookPreset:
            case SmvmActionType.ExportLookPreset:
            case SmvmActionType.LoadLookLut:
                await ExecuteLookActionAsync(action, actionStillCurrent);
                break;
            case SmvmActionType.SetCustomFogEnabled:
                if (action.Value is 0.0 or 1.0)
                {
                    var enableFog = action.Value == 1.0;
                    if (_movieVisual.SetFogEnabled(enableFog))
                    {
                        _settings.SmvmCustomFogEnabled = enableFog;
                        _settings.Save();
                    }
                }
                break;
            case SmvmActionType.SetCustomFogValue:
                var currentFog = _movieVisual.State.Fog;
                var updatedFog = action.Index switch
                {
                    0 => currentFog with
                    {
                        Start = action.Value,
                        End = Math.Max(currentFog.End, action.Value + 100),
                    },
                    1 => currentFog with
                    {
                        Start = Math.Min(currentFog.Start, action.Value - 100),
                        End = action.Value,
                    },
                    2 => currentFog with { MaximumDensity = action.Value },
                    3 => currentFog with { Exponent = action.Value },
                    _ => currentFog,
                };
                if (_movieVisual.SetFog(updatedFog))
                {
                    _settings.SmvmCustomFog = updatedFog;
                    _settings.Save();
                }
                break;
            case SmvmActionType.SetCustomFogColor:
                var color = checked((uint)action.Index);
                var fogBeforeColor = _movieVisual.State.Fog;
                var coloredFog = fogBeforeColor with
                {
                    Red = (byte)((color >> 16) & 0xFF),
                    Green = (byte)((color >> 8) & 0xFF),
                    Blue = (byte)(color & 0xFF),
                };
                if (_movieVisual.SetFog(coloredFog))
                {
                    _settings.SmvmCustomFog = coloredFog;
                    _settings.Save();
                }
                break;
            case SmvmActionType.ResetCustomFog:
                if (_movieVisual.ApplyFogPreset(FogConfiguration.Default))
                {
                    _settings.SmvmCustomFog = FogConfiguration.Default;
                    _settings.Save();
                }
                break;
            case SmvmActionType.SetGreenscreenMode:
                if (Enum.IsDefined((GreenscreenMode)action.Index) &&
                    _movieVisual.SetGreenscreenMode((GreenscreenMode)action.Index))
                {
                    _settings.SmvmGreenscreenMode = (GreenscreenMode)action.Index;
                    _settings.Save();
                }
                break;
            case SmvmActionType.StartMovieRecording:
                _ = RunReplayEffect(() => _ = ArmCinematicRecording());
                break;
            case SmvmActionType.StopMovieRecording:
                Interlocked.Exchange(ref _compositingCancelled, 1);
                _movieRecording.Stop();
                RestoreGreenscreenAfterCompositing();
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
                // Pause/resume can recreate Panorama, but it does not reset the
                // entity presentation profile. Reassert only the two cheap
                // runtime controls; the observed state edge does the same once
                // more after the engine settles.
                ReassertDeadlockUiIfCurrent(actionStillCurrent);
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
                var recordingArmedFromStart = _movieRecording.State.IsArmed;
                try
                {
                    await _campath.PlayAsync(
                        CampathPlayMode.FromStart,
                        recordingArmedFromStart ? StartArmedCinematicWithMovieVisuals : null)
                        .ConfigureAwait(true);
                }
                finally
                {
                    // Readiness failures happen after the native writers start
                    // but before CampathPlaying becomes true. Always unwind the
                    // take in that gap so timing and visual settings are restored.
                    if (recordingArmedFromStart && !_native.CampathPlaying)
                        _movieRecording.StopCinematicRecording();
                }
                break;
            case SmvmActionType.PlayFromCurrent:
                await EnterSmvmFreeCameraAsync().ConfigureAwait(true);
                if (!actionStillCurrent())
                    return;
                var recordingArmedFromCurrent = _movieRecording.State.IsArmed;
                try
                {
                    await _campath.PlayAsync(
                        CampathPlayMode.FromCurrent,
                        recordingArmedFromCurrent ? StartArmedCinematicWithMovieVisuals : null)
                        .ConfigureAwait(true);
                }
                finally
                {
                    if (recordingArmedFromCurrent && !_native.CampathPlaying)
                        _movieRecording.StopCinematicRecording();
                }
                break;
            case SmvmActionType.StopCampath:
                var interruptingMovieTake = _movieRecording.State.IsRecording ||
                                            _movieRecording.State.IsArmed;
                Interlocked.Exchange(ref _compositingCancelled, 1);
                if (interruptingMovieTake)
                {
                    // Escape reaches this typed path while a take is live. Freeze
                    // transport before releasing Campath ownership so no
                    // uncontrolled replay frames can enter the writer during
                    // cleanup, then end/finalize the current take. The ordinary
                    // Stop Campath command keeps its historical transport
                    // behavior when no recording exists.
                    if (replayCommandLease is { } moviePauseLease)
                        _ = _controller.PauseIfCurrent(moviePauseLease);
                    _movieRecording.Stop();
                }
                if (_native.CampathCameraOwned)
                    await _campath.StopAsync().ConfigureAwait(true);
                else if (!interruptingMovieTake)
                    await _campath.StopAsync().ConfigureAwait(true);
                _movieRecording.StopCinematicRecording();
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
            case SmvmActionType.ExitDeadlock:
            {
                var terminated = DeadlockProcessControl.ForceExit(out var exitError);
                if (terminated > 0)
                    _log.Info($"Exit Deadlock: force-terminated {terminated} Deadlock process(es) from the in-game menu.");
                else
                    _log.Warn($"Exit Deadlock: no running Deadlock process was found. {exitError}");
                break;
            }
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
                _ = ReassertSuppressionForPresentationLease(
                    IsReplayActive(_controller.State),
                    stillCurrent);
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
                    $"Replay speed requested: {speed * 100.0:0.##}% through host_timescale; " +
                    "engine readback will reconcile the displayed value.");
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
                    () => ReassertFullPresentationForLease(
                        replayActive: true,
                        () => stillCurrent() && IsProcessBoundaryCurrent(processEpoch)))
                : ReassertFullPresentationForLease(
                    replayActive: true, stillCurrent),
            "full recording-profile reassert",
            processBoundaryEpoch is { } epoch
                ? () => IsProcessBoundaryCurrent(epoch)
                : null).ConfigureAwait(true);
    }

    private async Task<bool> ReassertRuntimeSuppressionWithConnectionLeaseAsync(
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
            "runtime HUD suppression reassert",
            processBoundaryEpoch is { } epoch
                ? () => IsProcessBoundaryCurrent(epoch)
                : null).ConfigureAwait(true);
    }

    private bool ReassertFullPresentationForLease(
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

    private bool ReassertSuppressionForPresentationLease(
        bool replayActive,
        Func<bool> stillCurrent)
    {
        var profile = _deadlockUi.ProfileStatus;
        if (profile.DesiredMode == DeadlockUiMode.DeadlockUi)
            return profile.Ui.Mode == DeadlockUiMode.DeadlockUi && !profile.RestorePending;
        if (profile.ShouldRetryForwardProfile)
        {
            // A failed thin refresh first restores Deadlock's physical state.
            // The next valid runtime edge performs one complete recovery rather
            // than letting a two-command refresh falsely retire that debt.
            return _deadlockUi.ApplyIfCurrent(
                profile.DesiredMode,
                replayActive,
                stillCurrent,
                profile.AcknowledgementGeneration);
        }
        return _deadlockUi.ReassertSuppressionIfCurrent(replayActive, stillCurrent);
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
            (127, "PageUp"), (128, "PageDown"), (129, "CapsLock"), (130, "Space"), (131, "Left"), (132, "Right"), (133, "Escape"), (134, "OemMinus"), (135, "OemPlus")
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
            (127, string.Empty), (128, string.Empty), (129, "CapsLock"), (130, "Space"), (131, "Left"), (132, "Right"), (133, "Escape"), (134, "OemMinus"), (135, "OemPlus")
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
                if ((item.Slot == 130 && other.Slot == 104) || (item.Slot == 104 && other.Slot == 130) || !BindingsConflict(other.Binding, binding))
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
        if (index is < 100 or > 135)
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
        foreach (var otherIndex in Enumerable.Range(100, 36))
        {
            if (otherIndex == index || (otherIndex == 104 && index == 130) || (otherIndex == 130 && index == 104) ||
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
        129 => _settings.SmvmEffectsHotkey,
        130 => _settings.SmvmCinematicStartHotkey,
        131 => _settings.SmvmPlaybackSlowerHotkey,
        132 => _settings.SmvmPlaybackFasterHotkey,
        133 => _settings.SmvmCancelHotkey,
        134 => _settings.SmvmCameraSlowerHotkey,
        135 => _settings.SmvmCameraFasterHotkey,

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
            case 129: _settings.SmvmEffectsHotkey = value; break;
            case 130: _settings.SmvmCinematicStartHotkey = value; break;
            case 131: _settings.SmvmPlaybackSlowerHotkey = value; break;
            case 132: _settings.SmvmPlaybackFasterHotkey = value; break;
            case 133: _settings.SmvmCancelHotkey = value; break;
            case 134: _settings.SmvmCameraSlowerHotkey = value; break;
            case 135: _settings.SmvmCameraFasterHotkey = value; break;

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
        129 => "Effects panel", 130 => "Start ready cinematic", 131 => "Slower replay",
        132 => "Faster replay", 133 => "Cancel recording / camera", 134 => "Slower camera", 135 => "Faster camera",
        _ => $"Slot {index}",
    };

    private bool StartArmedCinematicWithMovieVisuals()
    {
        // Native fog is composited from the retained world-depth surface on
        // every Present, before the pass writer reads Beauty. It needs no
        // seek-time or recording-time console reassertion. A dedicated green
        // pass does own physical world/sky/viewmodel commands, so fail the
        // cinematic start if that plate cannot be established.
        var targetGreenscreen =
            (_movieRecording.State.ActivePasses & MovieCapturePass.GreenscreenFreeCamera) != 0
                ? GreenscreenMode.FreeCamera
                : GreenscreenMode.Off;
        if (_movieVisual.State.Greenscreen != targetGreenscreen &&
            !_movieVisual.SetGreenscreenMode(targetGreenscreen))
        {
            return false;
        }
        return _movieRecording.StartArmedCinematic();
    }

    private bool ArmCinematicRecording()
    {
        Interlocked.Exchange(ref _compositingCancelled, 0);
        return _movieRecording.ArmForCinematic(BuildCinematicCaptureName());
    }

    private void RestoreGreenscreenAfterCompositing()
    {
        _compositingChromaStopTick = null;
        if (_movieVisual.State.Greenscreen != GreenscreenMode.Off)
            _ = _movieVisual.SetGreenscreenMode(GreenscreenMode.Off);
        if (_settings.SmvmGreenscreenMode != GreenscreenMode.Off)
        {
            _settings.SmvmGreenscreenMode = GreenscreenMode.Off;
            _settings.Save();
        }
    }

    private static long? ReadCaptureMetric(
        IReadOnlyList<string> lines,
        string prefix)
    {
        var line = lines.FirstOrDefault(value =>
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (line is null)
            return null;
        return long.TryParse(
            line[prefix.Length..].Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static bool ManifestPassComplete(
        IReadOnlyList<string> lines,
        string prefix) =>
        lines.Any(value =>
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            value.Contains("complete (", StringComparison.OrdinalIgnoreCase));

    private static ulong? ReadCaptureHexMetric(
        IReadOnlyList<string> lines,
        string prefix)
    {
        var line = lines.FirstOrDefault(value =>
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (line is null)
            return null;
        var text = line[prefix.Length..].Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return ulong.TryParse(
            text,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static bool WorldVisualPassesComplete(
        string groupDirectory,
        MovieCapturePass selectedPasses)
    {
        try
        {
            var worldDirectory = Path.Combine(groupDirectory, "world");
            var manifestPath = Path.Combine(worldDirectory, "deadlockmvm_capture.txt");
            if (!File.Exists(manifestPath))
                return false;
            var lines = File.ReadAllLines(manifestPath);
            var complete = File.Exists(Path.Combine(worldDirectory, "avi", "world.avi")) &&
                           ManifestPassComplete(lines, "World AVI:");
            if ((selectedPasses & MovieCapturePass.WorldDepthAvi) != 0)
                complete &= File.Exists(Path.Combine(worldDirectory, "avi", "zdepth.avi")) &&
                            ManifestPassComplete(lines, "Z-Depth preview AVI:");
            if ((selectedPasses & MovieCapturePass.WorldDepthPfm) != 0)
                complete &= ManifestPassComplete(lines, "Z-Depth PFM:");
            return complete;
        }
        catch
        {
            return false;
        }
    }

    private bool WriteCompositingTakeReport(
        string groupDirectory,
        MovieCapturePass selectedPasses,
        out string detail)
    {
        detail = "The synchronized take needs manual review.";
        try
        {
            if (string.IsNullOrWhiteSpace(groupDirectory) || !Directory.Exists(groupDirectory))
                return false;
            var worldDirectory = Path.Combine(groupDirectory, "world");
            var chromaDirectory = Path.Combine(groupDirectory, "chroma");
            var worldManifestPath = Path.Combine(worldDirectory, "deadlockmvm_capture.txt");
            var chromaManifestPath = Path.Combine(chromaDirectory, "deadlockmvm_capture.txt");
            if (!File.Exists(worldManifestPath) || !File.Exists(chromaManifestPath))
                return false;
            var worldManifest = File.ReadAllLines(worldManifestPath);
            var chromaManifest = File.ReadAllLines(chromaManifestPath);
            var worldFrames = ReadCaptureMetric(worldManifest, "Frames observed:");
            var chromaFrames = ReadCaptureMetric(chromaManifest, "Frames observed:");
            var worldFirstTick = ReadCaptureMetric(worldManifest, "First replay tick:");
            var chromaFirstTick = ReadCaptureMetric(chromaManifest, "First replay tick:");
            var worldLastTick = ReadCaptureMetric(worldManifest, "Last replay tick:");
            var chromaLastTick = ReadCaptureMetric(chromaManifest, "Last replay tick:");
            var worldTimingDigest = ReadCaptureHexMetric(
                worldManifest,
                "Replay timing digest:");
            var chromaTimingDigest = ReadCaptureHexMetric(
                chromaManifest,
                "Replay timing digest:");
            var worldCameraSamples = ReadCaptureMetric(
                worldManifest,
                "Rendered camera samples:");
            var chromaCameraSamples = ReadCaptureMetric(
                chromaManifest,
                "Rendered camera samples:");
            var worldCameraDigest = ReadCaptureHexMetric(
                worldManifest,
                "Rendered camera digest:");
            var chromaCameraDigest = ReadCaptureHexMetric(
                chromaManifest,
                "Rendered camera digest:");
            var chromaBackgroundPixels = ReadCaptureMetric(
                chromaManifest,
                "Green Screen background pixels:");
            var chromaSubjectPixels = ReadCaptureMetric(
                chromaManifest,
                "Green Screen subject pixels:");
            var chromaFramesWithBackground = ReadCaptureMetric(
                chromaManifest,
                "Green Screen frames with key background:");
            var chromaFramesWithSubject = ReadCaptureMetric(
                chromaManifest,
                "Green Screen frames with isolated subject:");
            var chromaRepeatedVisualSamples = ReadCaptureMetric(
                chromaManifest,
                "Repeated sampled visual frames observed:");

            var worldAvi = Path.Combine(worldDirectory, "avi", "world.avi");
            var depthAvi = Path.Combine(worldDirectory, "avi", "zdepth.avi");
            var chromaAvi = Path.Combine(chromaDirectory, "avi", "chroma.avi");
            var wave = Path.Combine(worldDirectory, "audio", "world.wav");
            var chromaStatic = chromaFrames is { } chromaFrameTotal &&
                               chromaFrameTotal > 0 &&
                               chromaRepeatedVisualSamples is { } chromaRepeats &&
                               chromaRepeats * 2 >= chromaFrameTotal;
            var greenPlateUsable = MovieCompositingAlignmentPolicy.IsGreenscreenPlateUsable(
                new MovieGreenscreenPlateMetrics(
                    chromaFrames,
                    chromaBackgroundPixels,
                    chromaSubjectPixels,
                    chromaFramesWithBackground,
                    chromaFramesWithSubject,
                    chromaRepeatedVisualSamples));
            var aligned = MovieCompositingAlignmentPolicy.IsFrameTickAndTimingAligned(
                new MoviePassAlignmentMetrics(
                    worldFrames,
                    worldFirstTick,
                    worldLastTick,
                    worldTimingDigest,
                    worldCameraSamples,
                    worldCameraDigest),
                new MoviePassAlignmentMetrics(
                    chromaFrames,
                    chromaFirstTick,
                    chromaLastTick,
                    chromaTimingDigest,
                    chromaCameraSamples,
                    chromaCameraDigest));
            var waveHasAudio = File.Exists(wave) && new FileInfo(wave).Length > 44;
            var visualLayersComplete = aligned &&
                                       greenPlateUsable &&
                                       File.Exists(worldAvi) &&
                                       File.Exists(chromaAvi) &&
                                       ManifestPassComplete(worldManifest, "World AVI:") &&
                                       ManifestPassComplete(chromaManifest, "Chroma AVI:");
            if ((selectedPasses & MovieCapturePass.WorldDepthAvi) != 0)
                visualLayersComplete &= File.Exists(depthAvi) &&
                                        ManifestPassComplete(
                                            worldManifest,
                                            "Z-Depth preview AVI:");
            if ((selectedPasses & MovieCapturePass.WorldDepthPfm) != 0)
                visualLayersComplete &= ManifestPassComplete(
                    worldManifest,
                    "Z-Depth PFM:");
            var complete = visualLayersComplete && waveHasAudio;

            var chromaMissing = chromaFrames is null or <= 0 || !File.Exists(chromaAvi);
            var status = complete
                ? "READY - FRAME SCHEDULE ALIGNED"
                : chromaMissing
                    ? "FAILED - GREEN SCREEN DID NOT RECORD"
                    : !greenPlateUsable
                        ? chromaStatic
                            ? "FAILED - GREEN SCREEN PLATE IS STATIC"
                            : "FAILED - GREEN SCREEN PLATE IS NOT USABLE"
                        : visualLayersComplete && !waveHasAudio
                            ? "VISUAL LAYERS READY - AUDIO MISSING"
                            : "REVIEW REQUIRED - SEE ALIGNMENT VALUES BELOW";
            File.WriteAllLines(
                Path.Combine(groupDirectory, "COMPOSITING_TAKE.txt"),
                [
                    "DeadLockMVM synchronized compositing take",
                    $"Status: {status}",
                    $"Visual layers: {(visualLayersComplete ? "ready" : "review required")}",
                    $"Audio: {(waveHasAudio ? "ready" : "missing or empty")}",
                    $"World frames: {worldFrames?.ToString(CultureInfo.InvariantCulture) ?? "missing"}",
                    $"Chroma frames: {chromaFrames?.ToString(CultureInfo.InvariantCulture) ?? "missing"}",
                    $"World ticks: {worldFirstTick?.ToString(CultureInfo.InvariantCulture) ?? "missing"} to {worldLastTick?.ToString(CultureInfo.InvariantCulture) ?? "missing"}",
                    $"Chroma ticks: {chromaFirstTick?.ToString(CultureInfo.InvariantCulture) ?? "missing"} to {chromaLastTick?.ToString(CultureInfo.InvariantCulture) ?? "missing"}",
                    $"World timing digest: {(worldTimingDigest is { } worldDigest ? $"0x{worldDigest:x16}" : "missing")}",
                    $"Chroma timing digest: {(chromaTimingDigest is { } chromaDigest ? $"0x{chromaDigest:x16}" : "missing")}",
                    $"World camera samples/digest: {worldCameraSamples?.ToString(CultureInfo.InvariantCulture) ?? "missing"} / {(worldCameraDigest is { } worldCameraHash ? $"0x{worldCameraHash:x16}" : "missing")}",
                    $"Chroma camera samples/digest: {chromaCameraSamples?.ToString(CultureInfo.InvariantCulture) ?? "missing"} / {(chromaCameraDigest is { } chromaCameraHash ? $"0x{chromaCameraHash:x16}" : "missing")}",
                    $"Green background pixels/frames: {chromaBackgroundPixels?.ToString(CultureInfo.InvariantCulture) ?? "missing"} / {chromaFramesWithBackground?.ToString(CultureInfo.InvariantCulture) ?? "missing"}",
                    $"Green subject pixels/frames: {chromaSubjectPixels?.ToString(CultureInfo.InvariantCulture) ?? "missing"} / {chromaFramesWithSubject?.ToString(CultureInfo.InvariantCulture) ?? "missing"}",
                    $"Chroma repeated samples/frames: {chromaRepeatedVisualSamples?.ToString(CultureInfo.InvariantCulture) ?? "missing"} / {chromaFrames?.ToString(CultureInfo.InvariantCulture) ?? "missing"}",
                    $"Audio payload: {(waveHasAudio ? $"{new FileInfo(wave).Length:N0} bytes" : "missing or empty")}",
                    string.Empty,
                    "EDITOR FILES",
                    "World: world\\avi\\world.avi",
                    "Z-Depth preview: world\\avi\\zdepth.avi",
                    "Z-Depth float sequence: world\\depth\\zdepth_########.pfm",
                    "Chroma: chroma\\avi\\chroma.avi",
                    "Audio: world\\audio\\world.wav",
                    string.Empty,
                    "LAYER ORDER",
                    "1. Put World on the bottom.",
                    "2. Put Chroma directly above World and key out green.",
                    "3. Keep Z-Depth hidden and use it only as a depth/blur control layer.",
                    "4. Apply world depth-of-field before compositing the keyed subject when the subject must stay sharp.",
                    string.Empty,
                    "All videos start at frame 0. Do not slip one pass independently.",
                ]);
            detail = complete
                ? $"Synchronized World, Z-Depth, Chroma, and WAV are aligned at {worldFrames:N0} frames."
                : chromaMissing
                    ? "World and Z-Depth were preserved, but Green Screen recorded no usable AVI."
                    : !greenPlateUsable
                        ? chromaStatic
                            ? "Green Screen AVI is effectively a static image and cannot key."
                            : "Green Screen AVI exists, but it contains no validated key background or isolated subject."
                        : visualLayersComplete && !waveHasAudio
                            ? "World, Z-Depth, and Green Screen are aligned; WAV is missing or empty."
                            : "The passes were preserved, but their alignment report requires review.";
            return complete;
        }
        catch (Exception ex)
        {
            detail = $"Could not validate synchronized passes: {ex.Message}";
            _log.Warn(detail);
            return false;
        }
    }

    private void SaveMovieRecordingSettings()
    {
        var state = _movieRecording.State;
        _settings.SmvmMovieCaptureFps = state.CaptureFps;
        _settings.SmvmMovieRecordingPreset = state.Preset;
        _settings.SmvmMovieOutputMode = state.OutputMode;
        _settings.SmvmMovieOutputResolution = state.OutputResolution;
        _settings.SmvmMovieCapturePasses = state.Passes;
        _settings.SmvmMovieDisablePostProcessing =
            state.IsEnabled(MovieRecordingOptions.DisablePostProcessing);
        _settings.SmvmMovieMuteDialogue =
            state.IsEnabled(MovieRecordingOptions.MuteDialogue);
        _settings.Save();
    }

    private string BuildCinematicCaptureName()
    {
        var keyframes = _campath.GetKeyframeSnapshot();
        var startTick = keyframes.Length > 0
            ? keyframes.Min(static keyframe => keyframe.DemoTick)
            : Math.Max(0, _controller.State.CurrentTick ?? 0);
        var endTick = keyframes.Length > 0
            ? keyframes.Max(static keyframe => keyframe.DemoTick)
            : startTick;
        return MovieRecordingController.BuildCinematicCaptureName(
            _controller.State.ReplayName,
            startTick,
            endTick,
            _movieRecording.State.CaptureFps);
    }

    private void ChooseMovieCaptureFolder()
    {
        if (_movieRecording.State.IsRecording || _movieRecording.State.IsArmed)
        {
            _log.Warn("Stop or cancel the current recording before changing its save location.");
            return;
        }
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose where Deadlock MVM saves cinematic captures",
                InitialDirectory = _movieRecording.CaptureRoot,
                Multiselect = false,
            };
            if (dialog.ShowDialog() != true)
                return;

            var selected = Path.GetFullPath(dialog.FolderName);
            var longestTake = Path.Combine(selected, new string('x', 63));
            if (selected.IndexOfAny(['"', '\r', '\n', ';']) >= 0 ||
                Encoding.UTF8.GetByteCount(longestTake) >= 192)
            {
                System.Windows.MessageBox.Show(
                    "Choose a shorter folder path without semicolons so Deadlock can receive the complete capture name safely.",
                    "Deadlock MVM",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            Directory.CreateDirectory(selected);
            _settings.SmvmMovieCaptureRoot = selected;
            _settings.Save();
            _log.Info($"Movie capture folder changed to: {selected}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not change the movie capture folder: {ex.Message}");
        }
    }

    private void OpenMovieCaptureFolder()
    {
        try
        {
            var state = _movieRecording.State;
            var path = !string.IsNullOrWhiteSpace(state.CaptureDirectory) &&
                       Directory.Exists(state.CaptureDirectory)
                ? state.CaptureDirectory
                : _movieRecording.CaptureRoot;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            _log.Info($"Opened movie capture folder: {path}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not open the movie capture folder: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;
        _native.SmvmActionReceived -= OnActionReceived;
        _native.StatusChanged -= OnNativeStatusChanged;
        _camera.CapabilitiesChanged -= OnCameraCapabilitiesChanged;
        _native.SmvmSnapshotProvider = null;
        _campath.EditorStateChanged -= OnEditorStateChanged;
        _native.CampathStateChanged -= OnCampathStateChanged;
        _controller.StateChanged -= OnReplayStateChanged;
        _controller.OutputSilenceExpected = null;
        // Invalidate every captured forward lease before cancellation/drain.
        // The final inverse must be the last presentation transaction, not the
        // middle of a forward operation that was already waiting on the gate.
        Interlocked.Increment(ref _presentationIntentEpoch);
        _stop.Cancel();
        await _actionGate.WaitAsync().ConfigureAwait(false);
        _actionGate.Release();
        await _pathPublishGate.WaitAsync().ConfigureAwait(false);
        _pathPublishGate.Release();
        _movieRecording.Stop();
        await _movieRecording.WaitForFinalizationAsync().ConfigureAwait(false);
        _movieVisual.RestorePhysicalVisuals();
        _deadlockUi.Restore(force: true);
        _actionGate.Dispose();
        _pathPublishGate.Dispose();
        _stop.Dispose();
    }
}
