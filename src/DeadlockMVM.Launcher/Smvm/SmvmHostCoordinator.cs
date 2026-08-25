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
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly SemaphoreSlim _pathPublishGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _editorGate = new();
    private readonly bool _captureDiagnosticsEnabled;
    private EditorSnapshot _editor;
    private int _editorRevision;
    private int _publishedRevision = -1;
    private int _replaySeekInFlight;
    private bool _nativeWasConnected;
    private string _observedReplayIdentity = string.Empty;
    private string _normalizedHostTimescaleIdentity = string.Empty;
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
        _native.SmvmSnapshotProvider = CreateSnapshot;
        _native.SmvmActionReceived += OnActionReceived;
        _native.StatusChanged += OnNativeStatusChanged;
        _campath.EditorStateChanged += OnEditorStateChanged;
        _native.CampathStateChanged += OnCampathStateChanged;
        _controller.StateChanged += OnReplayStateChanged;
        _ = PublishEditorPathAsync();
        CoordinateDemoStartup(_controller.State);
    }

    private void OnActionReceived(object? sender, SmvmAction action)
    {
        if (IsDemoStartupOwnerOverride(action))
            _demoStartup.MarkOwnerOverride();
        // Explicit exits win immediately over any seek/self-test already ahead
        // of this action in the serialized queue. Native cleanup remains ordered.
        if (IsExplicitFreeCameraExitAction(action.Type))
            _native.CancelManualCameraIntent();
        if (action.Type == SmvmActionType.RestoreDeadlockUi)
        {
            // F9 is an emergency presentation escape. Restore Panorama at
            // ingress instead of making it wait behind a seek or self-test;
            // the queued handler still performs ordered camera cleanup.
            _deadlockUi.Restore(force: true);
        }
        _dispatcher.BeginInvoke(() => _ = ExecuteActionSafeAsync(action));
    }

    internal static bool IsExplicitFreeCameraExitAction(SmvmActionType action) =>
        action is SmvmActionType.ToggleManualCamera or SmvmActionType.RestoreDeadlockUi or
            SmvmActionType.PreviousPlayer or SmvmActionType.NextPlayer or
            SmvmActionType.InEye or SmvmActionType.Chase;

    private static bool IsDemoStartupOwnerOverride(SmvmAction action) =>
        IsExplicitFreeCameraExitAction(action.Type) ||
        action.Type is SmvmActionType.CycleReplayInterface ||
        (action.Type == SmvmActionType.SetDeadlockUiMode &&
         action.Index == (int)DeadlockUiMode.DeadlockUi);

    private void OnEditorStateChanged(object? sender, EventArgs e)
    {
        RefreshEditorSnapshot();
        _ = PublishEditorPathAsync();
    }

    private void OnNativeStatusChanged(object? sender, EventArgs e)
    {
        var connected = _native.Connected;
        if (connected && !_nativeWasConnected)
        {
            _nativeWasConnected = true;
            _ = PublishEditorPathAsync();
        }
        else if (!connected)
        {
            _nativeWasConnected = false;
        }
        _dispatcher.BeginInvoke(() => CoordinateDemoStartup(_controller.State));
    }

    private void OnCampathStateChanged(object? sender, CampathPlaybackStatus status) =>
        _dispatcher.BeginInvoke(RefreshEditorSnapshot);

    private void OnReplayStateChanged(object? sender, ReplayState state) =>
        _dispatcher.BeginInvoke(() =>
        {
            RefreshEditorSnapshot();
            ReassertMovieUiAfterTransportBoundary(state);
            CoordinateDemoStartup(state);
        });

    private void ReassertMovieUiAfterTransportBoundary(ReplayState replay)
    {
        if (!IsReplayActive(replay))
        {
            _observedReplayIdentity = string.Empty;
            _observedReplayPaused = null;
            return;
        }

        var identity = DemoStartupPolicy.CreateReplayIdentity(replay.ReplayName!, replay.TotalTicks);
        if (!string.Equals(identity, _observedReplayIdentity, StringComparison.Ordinal))
        {
            _observedReplayIdentity = identity;
            _observedReplayPaused = replay.IsPaused;
            return;
        }

        var pauseChanged = replay.IsPaused is not null && _observedReplayPaused is not null &&
                           replay.IsPaused != _observedReplayPaused;
        if (replay.IsPaused is not null)
            _observedReplayPaused = replay.IsPaused;
        if (pauseChanged)
            _deadlockUi.ReassertSuppression(replayActive: true);
    }

    private void CoordinateDemoStartup(ReplayState replay)
    {
        var replayActive = IsReplayActive(replay);
        var replayIdentity = replayActive
            ? DemoStartupPolicy.CreateReplayIdentity(replay.ReplayName!, replay.TotalTicks)
            : string.Empty;
        var directives = _demoStartup.Observe(
            new DemoStartupObservation(
                replayActive,
                replayIdentity,
                replay.IsPaused == true,
                _deadlockUi.State.Mode is DeadlockUiMode.SmvmReplayUi or DeadlockUiMode.CleanFootage,
                _native.Connected,
                _controller.GameTickOffset is not null,
                _native.ManualCameraEstablished),
            DateTimeOffset.UtcNow);

        if (!replayActive)
        {
            _normalizedHostTimescaleIdentity = string.Empty;
            if (_deadlockUi.State.Mode != DeadlockUiMode.DeadlockUi)
                _deadlockUi.Restore();
            return;
        }

        // host_timescale is process-global and can survive replay transitions.
        // Establish the owner's 100% baseline once for every newly observed
        // demo, then leave later custom choices alone for that demo session.
        if (!string.Equals(
                _normalizedHostTimescaleIdentity,
                replayIdentity,
                StringComparison.Ordinal))
        {
            _normalizedHostTimescaleIdentity = replayIdentity;
            try
            {
                _controller.SetSpeed(1.0);
                _log.Info("Demo startup: host_timescale reset to 100%.");
            }
            catch (Exception ex)
            {
                _normalizedHostTimescaleIdentity = string.Empty;
                _log.Warn($"Demo startup: host_timescale reset failed: {ex.Message}");
            }
        }

        if (directives.PauseDemo)
        {
            try
            {
                _controller.Pause();
                _log.Info("Demo startup: pause requested.");
            }
            catch (Exception ex)
            {
                _demoStartup.MarkPauseCommandFailed(directives.Generation);
                _log.Warn($"Demo startup: pause request failed: {ex.Message}");
            }
        }

        if (directives.HideGameHud)
        {
            var hidden = _deadlockUi.Apply(DeadlockUiMode.SmvmReplayUi, replayActive: true);
            _demoStartup.MarkHudAttemptCompleted(
                directives.Generation,
                hidden,
                DateTimeOffset.UtcNow);
            if (hidden)
                _log.Info("Demo startup: Deadlock HUD hidden.");
        }

        if (directives.EnterFreeCamera)
            _ = EnterAutomaticFreeCameraAsync(directives.Generation);
    }

    private async Task EnterAutomaticFreeCameraAsync(int generation)
    {
        var acquired = false;
        var success = false;
        try
        {
            await _actionGate.WaitAsync(_stop.Token).ConfigureAwait(true);
            acquired = true;
            if (!_demoStartup.IsCurrent(generation) || !IsReplayActive(_controller.State))
                return;
            await EnterSmvmFreeCameraAsync().ConfigureAwait(true);
            success = _native.ManualCameraEstablished;
            if (success)
                _log.Info("Demo startup: SMVM Free Camera active; Deadlock gameplay input is suppressed.");
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
            _demoStartup.MarkFreeCameraAttemptCompleted(
                generation,
                success,
                DateTimeOffset.UtcNow);
        }

        if (!success && _demoStartup.IsCurrent(generation) && !_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, _stop.Token).ConfigureAwait(false);
                _ = _dispatcher.BeginInvoke(() => CoordinateDemoStartup(_controller.State));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }
    }

    private static bool IsReplayActive(ReplayState replay) =>
        replay.Connected && !string.IsNullOrWhiteSpace(replay.ReplayName) &&
        replay.CurrentTick is not null &&
        (replay.TotalTicks is null || replay.CurrentTick < replay.TotalTicks);

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
        var internalEnabled = true; // The internal SMVM editor is the only editor.
        var replayActive = IsReplayActive(replay);
        var deadlockUi = _deadlockUi.State;
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
            _settings.SmvmStatusHudOpacity);
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
        var acquired = false;
        try
        {
            acquired = await _pathPublishGate.WaitAsync(0, _stop.Token).ConfigureAwait(false);
            if (!acquired)
                return;
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
        }
    }

    private async Task ExecuteActionSafeAsync(SmvmAction action)
    {
        var acquired = false;
        try
        {
            await _actionGate.WaitAsync(_stop.Token).ConfigureAwait(true);
            acquired = true;
            await ExecuteActionAsync(action).ConfigureAwait(true);
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

    private async Task ExecuteActionAsync(SmvmAction action)
    {
        switch (action.Type)
        {
            case SmvmActionType.CaptureDiagnostic:
            {
                var stage = (SmvmCaptureStage)action.Index;
                var rejection = (SmvmCaptureRejection)action.Tick;
                TraceCapture(stage, rejection);
                if (stage == SmvmCaptureStage.CaptureRejected)
                    _campath.ReportCaptureRejection(rejection);
                break;
            }
            case SmvmActionType.SetDeadlockUiMode:
                ApplyDeadlockUiMode((DeadlockUiMode)action.Index);
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
                    _settings.SmvmDeadlockUiMode = DeadlockUiMode.DeadlockUi;
                    _settings.Save();
                    _deadlockUi.Restore(force: true);
                }
                break;
            case SmvmActionType.CycleReplayInterface:
            {
                var current = _deadlockUi.State.Mode == DeadlockUiMode.CleanFootage
                    ? _deadlockUi.State.PreviousVisibleMode
                    : _deadlockUi.State.Mode;
                ApplyDeadlockUiMode(current == DeadlockUiMode.DeadlockUi
                    ? DeadlockUiMode.SmvmReplayUi
                    : DeadlockUiMode.DeadlockUi);
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
                if (action.Index == 1)
                    _controller.Pause();
                else if (action.Index == 0)
                    _controller.Play();
                else
                    _controller.TogglePause();
                // Send suppression after the transport command in the same
                // VConsole ordering window. The observed pause-state edge
                // reasserts once more after the engine settles.
                _deadlockUi.ReassertSuppression(IsReplayActive(_controller.State));
                break;
            case SmvmActionType.SetTimescale:
                _controller.SetSpeed(action.Value);
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
                _camera.SelectPrevPlayer();
                break;
            case SmvmActionType.NextPlayer:
                await ExitSmvmFreeCameraAsync().ConfigureAwait(true);
                _camera.SelectNextPlayer();
                break;
            case SmvmActionType.InEye:
                await ExitSmvmFreeCameraAsync().ConfigureAwait(true);
                _camera.SelectInEye();
                break;
            case SmvmActionType.Chase:
                await ExitSmvmFreeCameraAsync().ConfigureAwait(true);
                _camera.SelectChase();
                break;
            case SmvmActionType.SetFov:
                // A stale overlay may still send this legacy action. The
                // external Deadlock FOV backend cannot update the in-process
                // manual sample, so fail closed until a true manual setter exists.
                break;
            case SmvmActionType.SaveCamera:
                await _camera.SaveCameraAsync(_stop.Token).ConfigureAwait(true);
                break;
            case SmvmActionType.RestoreCamera:
                if (!_native.CameraOwned)
                    await _camera.RestoreCameraAsync(_stop.Token).ConfigureAwait(true);
                break;
            case SmvmActionType.AddKeyframe:
                if (action.Tick >= 0 && action.Camera.IsValid)
                {
                    if (!_native.ManualCameraEstablished)
                    {
                        TraceCapture(
                            SmvmCaptureStage.CaptureRejected,
                            SmvmCaptureRejection.NotInFreeRoam,
                            "SMVM Free Camera is not active");
                        _campath.ReportCaptureRejection(SmvmCaptureRejection.NotInFreeRoam);
                        break;
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
                }
                break;
            case SmvmActionType.DeleteKeyframe:
                _campath.DeleteKeyframe(action.Index);
                break;
            case SmvmActionType.SelectKeyframe:
                _campath.SelectKeyframe(action.Index);
                break;
            case SmvmActionType.GoToKeyframe:
                await EnterSmvmFreeCameraAsync().ConfigureAwait(true);
                _campath.SelectKeyframe(action.Index);
                await _campath.GoToAsync().ConfigureAwait(true);
                break;
            case SmvmActionType.UpdateKeyframe:
                _campath.SelectKeyframe(action.Index);
                await _campath.UpdateAsync().ConfigureAwait(true);
                break;
            case SmvmActionType.ClearPath:
                _campath.RequestClear();
                break;
            case SmvmActionType.SetInterpolation:
                _campath.InterpolationMode = action.Index == 1
                    ? CampathInterpolationMode.Smooth
                    : CampathInterpolationMode.Linear;
                break;
            case SmvmActionType.SetEasing:
                if (Enum.IsDefined((CampathEasingMode)action.Index))
                    _campath.EasingMode = (CampathEasingMode)action.Index;
                break;
            case SmvmActionType.PlayFromStart:
                await EnterSmvmFreeCameraAsync().ConfigureAwait(true);
                await _campath.PlayAsync(CampathPlayMode.FromStart).ConfigureAwait(true);
                break;
            case SmvmActionType.PlayFromCurrent:
                await EnterSmvmFreeCameraAsync().ConfigureAwait(true);
                await _campath.PlayAsync(CampathPlayMode.FromCurrent).ConfigureAwait(true);
                break;
            case SmvmActionType.StopCampath:
                await _campath.StopAsync().ConfigureAwait(true);
                break;
            case SmvmActionType.SetEndBehavior:
                _campath.EndBehavior = action.Index == 1
                    ? CampathEndBehavior.HoldFinalCamera
                    : CampathEndBehavior.StopAndRelease;
                break;
            case SmvmActionType.UndoEdit:
                _campath.UndoCommand.Execute(null);
                break;
            case SmvmActionType.RedoEdit:
                _campath.RedoCommand.Execute(null);
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
                    _campath.PathName = action.Text;
                break;
            case SmvmActionType.SavePath:
                _campath.SaveCurrent();
                break;
            case SmvmActionType.LoadNextPath:
                _campath.LoadNextMatching();
                break;
            case SmvmActionType.NewPath:
                _campath.NewPath();
                break;
            case SmvmActionType.SavePathAs:
                _campath.SaveAs(action.Text);
                break;
            case SmvmActionType.LoadPath:
                _campath.LoadPathByIndex(action.Index);
                break;
            case SmvmActionType.ClosePath:
                _campath.ClosePath();
                break;
            case SmvmActionType.RecoverDraft:
                _campath.RecoverDraft();
                break;
            case SmvmActionType.DiscardDraft:
                _campath.DiscardDraft();
                break;
            case SmvmActionType.RequestPathList:
                _ = PublishDocumentsAsync();
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
                await _native.RunCampathSelfTestAsync(
                    new CampathPath(
                        _campath.GetKeyframeSnapshot(),
                        _campath.InterpolationMode,
                        _campath.EasingMode),
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

    private void ApplyDeadlockUiMode(DeadlockUiMode mode)
    {
        var replayActive = IsReplayActive(_controller.State);
        if (!_deadlockUi.Apply(mode, replayActive))
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

    private async Task ExecuteReplaySeekAsync(int tick)
    {
        Interlocked.Exchange(ref _replaySeekInFlight, 1);
        try
        {
            await _native.SeekReplayAsync(tick, _stop.Token).ConfigureAwait(true);
        }
        finally
        {
            Volatile.Write(ref _replaySeekInFlight, 0);
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
        _deadlockUi.Restore(force: true);
        _native.SmvmActionReceived -= OnActionReceived;
        _native.StatusChanged -= OnNativeStatusChanged;
        _native.SmvmSnapshotProvider = null;
        _campath.EditorStateChanged -= OnEditorStateChanged;
        _native.CampathStateChanged -= OnCampathStateChanged;
        _controller.StateChanged -= OnReplayStateChanged;
        _stop.Cancel();
        await _actionGate.WaitAsync().ConfigureAwait(false);
        _actionGate.Release();
        await _pathPublishGate.WaitAsync().ConfigureAwait(false);
        _pathPublishGate.Release();
        _actionGate.Dispose();
        _pathPublishGate.Dispose();
        _stop.Dispose();
    }
}
