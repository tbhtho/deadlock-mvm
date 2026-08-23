using System.Windows.Threading;
using System.IO;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Native.InProcess;
using DeadlockMVM.Core.Services;

namespace DeadlockMVM.Launcher.Director;

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
        CampathEndBehavior EndBehavior);

    private readonly ICameraService _camera;
    private readonly ReplayController _controller;
    private readonly NativeReplayCameraSession _native;
    private readonly CampathViewModel _campath;
    private readonly IAppSettings _settings;
    private readonly ILogService _log;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly SemaphoreSlim _pathPublishGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _editorGate = new();
    private EditorSnapshot _editor;
    private int _editorRevision;
    private int _publishedRevision = -1;
    private int _replaySeekInFlight;
    private bool _nativeWasConnected;

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
        _dispatcher = dispatcher;
        _editor = ReadEditorSnapshot();
        _native.SmvmSnapshotProvider = CreateSnapshot;
        _native.SmvmActionReceived += OnActionReceived;
        _native.StatusChanged += OnNativeStatusChanged;
        _campath.EditorStateChanged += OnEditorStateChanged;
        _native.CampathStateChanged += OnCampathStateChanged;
        _controller.StateChanged += OnReplayStateChanged;
        _ = PublishEditorPathAsync();
    }

    private void OnActionReceived(object? sender, SmvmAction action) =>
        _dispatcher.BeginInvoke(() => _ = ExecuteActionSafeAsync(action));

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
    }

    private void OnCampathStateChanged(object? sender, CampathPlaybackStatus status) =>
        _dispatcher.BeginInvoke(RefreshEditorSnapshot);

    private void OnReplayStateChanged(object? sender, ReplayState state) =>
        _dispatcher.BeginInvoke(RefreshEditorSnapshot);

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
            _campath.EndBehavior);
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
        var internalEnabled = _settings.InterfaceMode is SmvmInterfaceMode.InternalSmvm or SmvmInterfaceMode.BothDeveloper;
        var replayActive = replay.Connected && !string.IsNullOrWhiteSpace(replay.ReplayName) &&
                           replay.CurrentTick is not null &&
                           (replay.TotalTicks is null || replay.CurrentTick < replay.TotalTicks);
        var ownership = ResolveCameraOwnership(replayActive, native);
        var availability = ResolveCameraAvailability(
            internalEnabled, replayActive, replay, native, playback, ownership);
        var capabilities = ResolveCapabilities(internalEnabled, native);
        var cameraReadable = replayActive && _native.Connected && native?.CameraObserved == true;
        var spectatorCameraWritable = availability == CameraAvailability.Ready &&
                                      ownership == CameraOwnership.DeadlockSpectator;

        var flags = SmvmSnapshotFlags.None;
        if (replayActive) flags |= SmvmSnapshotFlags.ReplayActive;
        if (replay.IsPaused is not null) flags |= SmvmSnapshotFlags.PauseKnown;
        if (replay.IsPaused == true) flags |= SmvmSnapshotFlags.Paused;
        if (cameraReadable) flags |= SmvmSnapshotFlags.CameraReadable;
        if (_camera.Capabilities.CanWriteActiveFov && spectatorCameraWritable)
            flags |= SmvmSnapshotFlags.FovWritable;
        if (cameraReadable && _native.Available && spectatorCameraWritable)
            flags |= SmvmSnapshotFlags.RollWritable;
        if (ownership == CameraOwnership.SmvmCampath) flags |= SmvmSnapshotFlags.CampathPlaying;
        if (ownership is CameraOwnership.SmvmManualCamera or CameraOwnership.SmvmRestore or CameraOwnership.SmvmCampath)
            flags |= SmvmSnapshotFlags.CameraOwned;
        if (editor.Keyframes.Length > 0) flags |= SmvmSnapshotFlags.EditorPath;
        if (internalEnabled) flags |= SmvmSnapshotFlags.InternalEnabled;
        if (_settings.SmvmShowToolbar) flags |= SmvmSnapshotFlags.ShowToolbar;
        if (_settings.SmvmShowPath) flags |= SmvmSnapshotFlags.ShowPath;
        if (_settings.SmvmShowCameras) flags |= SmvmSnapshotFlags.ShowCameras;
        if (_settings.SmvmShowLabels) flags |= SmvmSnapshotFlags.ShowLabels;
        if (_settings.SmvmFovWheelInverted) flags |= SmvmSnapshotFlags.FovInverted;
        if (replayActive && _native.Connected && native?.ManualCameraRequested == true)
            flags |= SmvmSnapshotFlags.ManualCameraRequested;
        if (replayActive && _native.Connected && _native.ManualCameraActive)
            flags |= SmvmSnapshotFlags.ManualCameraActive;
        if (_settings.SmvmCameraInputTakeover && _native.ManualCameraActive)
            flags |= SmvmSnapshotFlags.InputTakeover;
        if (_settings.SmvmMouseInvertY) flags |= SmvmSnapshotFlags.InvertY;
        if (_settings.SmvmShowMinimalPill) flags |= SmvmSnapshotFlags.ShowMinimalPill;
        if (_settings.SmvmNotificationsEnabled) flags |= SmvmSnapshotFlags.Notifications;
        if (_settings.SmvmHidePathWhilePlaying) flags |= SmvmSnapshotFlags.HidePathWhilePlaying;

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
            SmvmInputCode.None,
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
            DescribeCameraAvailability(availability));
    }

    private CameraOwnership ResolveCameraOwnership(bool replayActive, InProcessCameraStatus? native)
    {
        if (!replayActive)
            return CameraOwnership.None;
        if (!_native.Connected)
            return CameraOwnership.DeadlockSpectator;
        if (_native.CampathPlaying || native?.Flags.HasFlag(InProcessStatusFlags.CampathActive) == true)
            return CameraOwnership.SmvmCampath;
        if (native?.ManualCameraRequested == true || native?.ManualCameraActive == true)
            return CameraOwnership.SmvmManualCamera;
        if (native?.OverrideActive == true || _native.CampathCameraOwned)
            return CameraOwnership.SmvmRestore;
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
            return CameraAvailability.OwnershipRejected;
        if (!native.CameraObserved)
            return CameraAvailability.CameraReadbackUnavailable;
        return CameraAvailability.Ready;
    }

    private SmvmCapabilities ResolveCapabilities(bool internalEnabled, InProcessCameraStatus? native)
    {
        if (!internalEnabled || !_native.Connected || native?.RendererBackend != SmvmRendererBackend.D3D11 ||
            !native.OverlayFlags.HasFlag(SmvmOverlayFlags.Ready))
            return SmvmCapabilities.None;

        var capabilities = SmvmCapabilities.PathVisualization;
        if (native.Flags.HasFlag(InProcessStatusFlags.Resolved) &&
            native.Flags.HasFlag(InProcessStatusFlags.HookInstalled))
        {
            capabilities |= SmvmCapabilities.ManualCamera;
            if (!_native.SelfTestStatus.IsRunning)
                capabilities |= SmvmCapabilities.CameraSelfTest | SmvmCapabilities.CampathSelfTest;
        }

        // Rendered roll remains unadvertised until this exact native build is
        // proven visually in the published application. The bounded self-tests
        // stay available so that live acceptance can produce that proof.
        return capabilities;
    }

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
            case SmvmActionType.ToggleReplayPause:
                _controller.TogglePause();
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
                await _camera.EnterFreeRoamAsync(_stop.Token).ConfigureAwait(true);
                break;
            case SmvmActionType.PreviousPlayer:
                _camera.SelectPrevPlayer();
                break;
            case SmvmActionType.NextPlayer:
                _camera.SelectNextPlayer();
                break;
            case SmvmActionType.InEye:
                _camera.SelectInEye();
                break;
            case SmvmActionType.Chase:
                _camera.SelectChase();
                break;
            case SmvmActionType.SetFov:
                if (!_native.CampathPlaying)
                    await _camera.SetActiveFovAsync(Math.Clamp(action.Value, 5.0, 170.0), _stop.Token)
                        .ConfigureAwait(true);
                break;
            case SmvmActionType.SaveCamera:
                await _camera.SaveCameraAsync(_stop.Token).ConfigureAwait(true);
                break;
            case SmvmActionType.RestoreCamera:
                if (_camera.SavedShot is { } savedShot && !_native.CampathPlaying && !_native.CameraOwned)
                    await _native.SetManualRollAsync(savedShot.Transform.Roll, _stop.Token).ConfigureAwait(true);
                await _camera.RestoreCameraAsync(_stop.Token).ConfigureAwait(true);
                break;
            case SmvmActionType.AddKeyframe:
                if (action.Tick >= 0 && action.Camera.IsValid)
                    _campath.AddAuthoritativeKeyframe(new CampathKeyframe(action.Tick, action.Camera));
                break;
            case SmvmActionType.DeleteKeyframe:
                _campath.DeleteKeyframe(action.Index);
                break;
            case SmvmActionType.SelectKeyframe:
                _campath.SelectKeyframe(action.Index);
                break;
            case SmvmActionType.GoToKeyframe:
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
                await _campath.PlayAsync(CampathPlayMode.FromStart).ConfigureAwait(true);
                break;
            case SmvmActionType.PlayFromCurrent:
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
                _settings.SmvmCameraInputTakeover = !_settings.SmvmCameraInputTakeover;
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
            case SmvmActionType.ToggleMinimalPill:
                _settings.SmvmShowMinimalPill = !_settings.SmvmShowMinimalPill;
                _settings.Save();
                break;
            case SmvmActionType.ToggleHidePathWhilePlaying:
                _settings.SmvmHidePathWhilePlaying = !_settings.SmvmHidePathWhilePlaying;
                _settings.Save();
                break;
            case SmvmActionType.ToggleManualCamera:
                if (_native.ManualCameraDesired || _native.ManualCameraActive)
                {
                    await _native.DisableManualCameraAsync(_stop.Token).ConfigureAwait(true);
                }
                else
                {
                    await _camera.EnterFreeRoamAsync(_stop.Token).ConfigureAwait(true);
                    await _native.EnableManualCameraAsync(_stop.Token).ConfigureAwait(true);
                }
                break;
            case SmvmActionType.ReacquireCamera:
                if (_native.CampathPlaying)
                    throw new InvalidOperationException("Stop Campath before reacquiring the manual camera.");
                if (_native.ManualCameraDesired || _native.ManualCameraActive)
                    await _native.DisableManualCameraAsync(_stop.Token).ConfigureAwait(true);
                await _camera.EnterFreeRoamAsync(_stop.Token).ConfigureAwait(true);
                await _native.EnableManualCameraAsync(_stop.Token).ConfigureAwait(true);
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

    private void ResetSmvmBindings()
    {
        _settings.SmvmMenuHotkey = "Tab";
        _settings.SmvmAddHotkey = "Mouse3";
        _settings.SmvmDeleteHotkey = "L";
        _settings.SmvmCleanViewHotkey = "F10";
        _settings.SmvmForwardHotkey = "W";
        _settings.SmvmBackHotkey = "S";
        _settings.SmvmLeftHotkey = "A";
        _settings.SmvmRightHotkey = "D";
        _settings.SmvmUpHotkey = "Space";
        _settings.SmvmDownHotkey = "VK11";
        _settings.SmvmFastHotkey = "VK10";
        _settings.SmvmPrecisionHotkey = "VK12";
        _settings.SmvmRollLeftHotkey = "Q";
        _settings.SmvmRollRightHotkey = "E";
        _settings.SmvmRollResetHotkey = "R";
        _settings.SmvmPlayStartHotkey = string.Empty;
        _settings.SmvmPlayCurrentHotkey = string.Empty;
        _settings.SmvmStopHotkey = string.Empty;
        _settings.SmvmUndoHotkey = "Ctrl+Z";
        _settings.SmvmRedoHotkey = "Ctrl+Y";
        _settings.SmvmShowPathHotkey = string.Empty;
        _settings.SmvmShowCamerasHotkey = string.Empty;
    }

    private void SetSmvmBinding(int index, double rawValue)
    {
        if (index is < 100 or > 121)
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
        foreach (var otherIndex in Enumerable.Range(100, 22))
        {
            if (otherIndex == index ||
                !InputBinding.TryParse(ReadSmvmBinding(otherIndex), out var existing) ||
                existing != binding)
                continue;

            _log.Warn($"SMVM binding rejected: {canonical} is assigned to {DescribeSmvmBinding(otherIndex)}.");
            return;
        }

        if (InputBinding.TryParse(_settings.DirectorHotkey, out var director) && director == binding)
        {
            _log.Warn($"SMVM binding rejected: {canonical} is assigned to Director.");
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
        114 => "Clean View",
        115 => "Play From Start",
        116 => "Play From Current",
        117 => "Stop",
        118 => "Undo",
        119 => "Redo",
        120 => "Show Path",
        121 => "Show Cameras",
        _ => $"Slot {index}",
    };

    public async ValueTask DisposeAsync()
    {
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
