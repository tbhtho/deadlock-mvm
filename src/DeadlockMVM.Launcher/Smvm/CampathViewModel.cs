using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Native.InProcess;
using DeadlockMVM.Core.Services;
using DeadlockMVM.Launcher.ViewModels;

namespace DeadlockMVM.Launcher.Smvm;

/// <summary>Compact replay-tick Campath editor; capture and playback are deliberately separate.</summary>
public sealed class CampathViewModel : INotifyPropertyChanged
{
    private sealed record EditState(
        CampathKeyframe[] Keyframes,
        long? SelectedTick,
        CampathInterpolationMode Interpolation,
        CampathEasingMode Easing,
        CampathEndBehavior EndBehavior);

    private readonly ICameraService _camera;
    private readonly ReplayController _controller;
    private readonly NativeReplayCameraSession _native;
    private readonly PassiveCampathCapture _capture;
    private readonly IAppSettings _settings;
    private readonly CampathStore _store;
    private readonly ILogService _log;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private CampathKeyframe? _selectedKeyframe;
    private CampathDocumentInfo? _selectedDocument;
    private string _status = "Fly in Free Roam and add camera keyframes.";
    private string _pathName = "Untitled Path";
    private string? _currentFilePath;
    private bool _isDraft;
    private bool _recoveryAvailable;
    private bool _suppressAutosave;
    private bool _operationInFlight;
    private DateTime _clearConfirmationDeadline;
    private CampathInterpolationMode _interpolationMode = CampathInterpolationMode.Linear;
    private CampathEasingMode _easingMode = CampathEasingMode.Linear;
    private CampathEndBehavior _endBehavior = CampathEndBehavior.StopAndRelease;
    private CampathReplayIdentifier? _lastReplayIdentifier;
    private readonly Stack<EditState> _undo = [];
    private readonly Stack<EditState> _redo = [];
    private bool _restoringHistory;

    public CampathViewModel(
        ICameraService camera,
        ReplayController controller,
        NativeReplayCameraSession native,
        IAppSettings settings,
        ILogService log,
        CampathStore? store = null)
    {
        _camera = camera;
        _controller = controller;
        _native = native;
        _capture = new PassiveCampathCapture(
            native.CaptureCurrentCameraAsync,
            () => new ReplayPlaybackConfiguration(controller.State.IsPaused, controller.State.Timescale));
        _settings = settings;
        _log = log;
        _store = store ?? new CampathStore();
        _dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);

        Keyframes = [];
        SavedCampaths = [];
        AddCommand = new RelayCommand(() => _ = AddAsync(), CanAdd);
        UpdateCommand = new RelayCommand(() => _ = UpdateAsync(), () => SelectedKeyframe is not null && CanCapture());
        DeleteCommand = new RelayCommand(DeleteSelected, () => SelectedKeyframe is not null && CanEditPath);
        ClearCommand = new RelayCommand(Clear, () => Keyframes.Count > 0 && CanEditPath);
        GoToCommand = new RelayCommand(() => _ = GoToAsync(), () => SelectedKeyframe is not null && _native.Available && !_operationInFlight);
        PlayCommand = new RelayCommand(() => _ = PlayAsync(CampathPlayMode.FromStart), () => Keyframes.Count >= 2 && _native.Available && CanEditPath);
        PlayCurrentCommand = new RelayCommand(() => _ = PlayAsync(CampathPlayMode.FromCurrent), () => Keyframes.Count >= 2 && _native.Available && CanEditPath);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => PathCameraOwned && !_operationInFlight);
        UndoCommand = new RelayCommand(Undo, () => _undo.Count > 0 && CanEditPath);
        RedoCommand = new RelayCommand(Redo, () => _redo.Count > 0 && CanEditPath);
        SaveCommand = new RelayCommand(Save, () => Keyframes.Count > 0 && CurrentReplayIdentifier() is not null && CanEditPath);
        LoadCommand = new RelayCommand(Load, () => SelectedDocument is not null && CurrentReplayIdentifier() is not null && CanEditPath);

        _native.StatusChanged += OnNativeStatusChanged;
        _native.CampathStateChanged += OnCampathStateChanged;
        _controller.StateChanged += OnReplayStateChanged;
        RefreshDocuments();
    }

    public ObservableCollection<CampathKeyframe> Keyframes { get; }
    public ObservableCollection<CampathDocumentInfo> SavedCampaths { get; }
    public IReadOnlyList<CampathInterpolationMode> InterpolationModes { get; } =
        Enum.GetValues<CampathInterpolationMode>();
    public IReadOnlyList<CampathEasingMode> EasingModes { get; } = Enum.GetValues<CampathEasingMode>();

    public CampathKeyframe? SelectedKeyframe
    {
        get => _selectedKeyframe;
        set
        {
            if (SetProperty(ref _selectedKeyframe, value))
            {
                RaiseCommandStates();
                OnEditorStateChanged();
            }
        }
    }

    public CampathDocumentInfo? SelectedDocument
    {
        get => _selectedDocument;
        set
        {
            if (SetProperty(ref _selectedDocument, value))
                RaiseCommandStates();
        }
    }

    public string PathName
    {
        get => _pathName;
        set
        {
            var clean = string.IsNullOrWhiteSpace(value) ? "Untitled Path" : value.Trim();
            if (PathCameraOwned || !SetProperty(ref _pathName, clean))
                return;
            Autosave();
            OnEditorStateChanged();
        }
    }

    /// <summary>Current workspace session; a normal startup is always <see cref="CampathSessionState.NoPath"/>.</summary>
    public CampathSessionState SessionState =>
        _currentFilePath is not null ? CampathSessionState.SavedPath :
        _isDraft ? CampathSessionState.DraftPath :
        CampathSessionState.NoPath;

    /// <summary>True while an abandoned on-disk draft from an abnormal shutdown can be recovered.</summary>
    public bool RecoveryAvailable
    {
        get => _recoveryAvailable;
        private set => SetProperty(ref _recoveryAvailable, value);
    }

    /// <summary>True when the draft holds unsaved keyframes that a New/Load/Close would discard.</summary>
    public bool HasUnsavedWork => SessionState == CampathSessionState.DraftPath && Keyframes.Count > 0;

    public CampathInterpolationMode InterpolationMode
    {
        get => _interpolationMode;
        set
        {
            if (PathCameraOwned || !Enum.IsDefined(value) || _interpolationMode == value)
                return;
            PushHistory();
            _interpolationMode = value;
            OnPropertyChanged(nameof(InterpolationMode));
            Autosave();
            OnEditorStateChanged();
        }
    }

    public CampathEasingMode EasingMode
    {
        get => _easingMode;
        set
        {
            if (PathCameraOwned || !Enum.IsDefined(value) || _easingMode == value)
                return;
            PushHistory();
            _easingMode = value;
            OnPropertyChanged(nameof(EasingMode));
            Autosave();
            OnEditorStateChanged();
        }
    }

    public CampathEndBehavior EndBehavior
    {
        get => _endBehavior;
        set
        {
            if (PathCameraOwned || !Enum.IsDefined(value) || value == CampathEndBehavior.Loop ||
                _endBehavior == value)
                return;
            PushHistory();
            _endBehavior = value;
            OnPropertyChanged(nameof(EndBehavior));
            Autosave();
            OnEditorStateChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string NativeState => $"NATIVE {_native.State.ToString().ToUpperInvariant()}";
    public string NativeMessage => _native.Message;
    public bool IsPlaying => _native.CampathPlaying;
    public bool CameraOwned => _native.CameraOwned;
    public bool PathCameraOwned => _native.CampathCameraOwned;
    public bool CanEditPath => !PathCameraOwned && !_operationInFlight;
    public string KeyframeCount => $"{Keyframes.Count} / {CampathPath.MaxKeyframes}";

    public ICommand AddCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand GoToCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand PlayCurrentCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand LoadCommand { get; }

    public event EventHandler? EditorStateChanged;

    public void ReportCaptureRejection(SmvmCaptureRejection rejection)
    {
        Status = rejection switch
        {
            SmvmCaptureRejection.ReplayUnavailable => "Keyframe capture rejected: replay playback is unavailable.",
            SmvmCaptureRejection.NotInFreeRoam => "Keyframe capture rejected: switch to Free Camera and reacquire Manual Camera.",
            SmvmCaptureRejection.CameraUnreadable => "Keyframe capture rejected: the rendered camera is not readable.",
            SmvmCaptureRejection.NativeBackendUnavailable => "Keyframe capture rejected: the native camera backend is unavailable.",
            SmvmCaptureRejection.CampathOwnsCamera => "Keyframe capture rejected: stop Campath playback first.",
            SmvmCaptureRejection.SnapshotStale => "Keyframe capture rejected: the editor snapshot is stale.",
            SmvmCaptureRejection.HookFrameStale => "Keyframe capture rejected: no fresh camera hook frame arrived.",
            SmvmCaptureRejection.CaptureAlreadyPending => "Keyframe capture rejected: another capture is already pending.",
            SmvmCaptureRejection.ConnectionEpochChanged => "Keyframe capture rejected: the native connection changed.",
            SmvmCaptureRejection.InvalidSample => "Keyframe capture rejected: the camera sample was invalid.",
            _ => "Keyframe capture rejected for an unclassified native reason. Check Advanced diagnostics.",
        };
        OnEditorStateChanged();
    }

    // At capacity we still allow one passive capture: the authoritative native
    // tick may replace an existing key. AddAuthoritativeKeyframe rejects only a
    // genuinely new 129th tick.
    private bool CanAdd() => CanCapture();

    private bool CanCapture() => CampathCaptureGate.CanCapture(new CampathCaptureAvailability(
        _controller.State.Connected,
        !string.IsNullOrWhiteSpace(_controller.State.ReplayName) && _controller.State.CurrentTick is not null,
        _native.Available,
        _controller.State.CurrentTick is not null,
        _camera.Selection.Mode == SpecCameraMode.FreeRoam,
        PathCameraOwned,
        _operationInFlight));

    private async Task AddAsync()
    {
        if (!CanAdd())
        {
            Status = PathCameraOwned
                ? "Add Keyframe is disabled while Campath owns the camera."
                : "Add Keyframe needs an active replay, Free Roam, and a readable native camera.";
            return;
        }

        var keyframe = await CaptureAsync().ConfigureAwait(true);
        if (keyframe is null)
            return;
        AddAuthoritativeKeyframe(keyframe, fromHotkey: false);
    }

    /// <summary>Adds an exact camera-hook sample without recapturing through the external UI.</summary>
    public void AddAuthoritativeKeyframe(CampathKeyframe keyframe, bool fromHotkey = true)
    {
        if (!keyframe.IsValid || PathCameraOwned || _operationInFlight ||
            (Keyframes.Count >= CampathPath.MaxKeyframes &&
             Keyframes.All(existing => existing.DemoTick != keyframe.DemoTick)))
        {
            Status = PathCameraOwned
                ? "Add Keyframe is disabled while Campath owns the camera."
                : Keyframes.Count >= CampathPath.MaxKeyframes
                    ? $"Campath is limited to {CampathPath.MaxKeyframes} keyframes."
                    : "The in-process camera sample was invalid or the editor is busy.";
            return;
        }
        PushHistory();
        var startedDraft = SessionState == CampathSessionState.NoPath;
        if (startedDraft)
            _isDraft = true; // First keyframe with no path loaded creates an Untitled draft.
        var replaced = CampathKeyframeEditor.Upsert(Keyframes, keyframe);
        SelectedKeyframe = keyframe;
        Status = !replaced
            ? startedDraft
                ? $"Draft created — keyframe 1 added at tick {keyframe.DemoTick}."
                : $"Keyframe {Keyframes.IndexOf(keyframe) + 1} added at tick {keyframe.DemoTick}."
            : $"Keyframe at tick {keyframe.DemoTick} updated.";
        _log.Info($"Campath: {Status}{(fromHotkey ? " (hotkey)" : string.Empty)}");
        OnCollectionChanged();
        Autosave();
        OnEditorStateChanged();
    }

    public async Task UpdateAsync()
    {
        var selected = SelectedKeyframe;
        if (selected is null || !CanCapture())
            return;
        var captured = await CaptureAsync().ConfigureAwait(true);
        if (captured is null)
            return;

        var index = Keyframes.IndexOf(selected);
        if (index < 0 || PathCameraOwned)
        {
            Status = "The selected keyframe changed while the camera was being captured; update cancelled.";
            return;
        }

        PushHistory();
        var updated = new CampathKeyframe(selected.DemoTick, captured.Camera);
        Keyframes[index] = updated;
        SelectedKeyframe = updated;
        Status = $"Updated camera at tick {updated.DemoTick}; timing kept unchanged.";
        Autosave();
        OnEditorStateChanged();
    }

    private async Task<CampathKeyframe?> CaptureAsync()
    {
        _operationInFlight = true;
        RaiseCommandStates();
        try
        {
            Status = "Capturing current camera…";
            return await _capture.CaptureAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warn($"Campath passive capture failed: {ex.Message}");
            Status = ex.Message;
            return null;
        }
        finally
        {
            _operationInFlight = false;
            RaiseCommandStates();
        }
    }

    public async Task PlayAsync(CampathPlayMode mode)
    {
        _operationInFlight = true;
        RaiseCommandStates();
        try
        {
            var path = new CampathPath(Keyframes, InterpolationMode, EasingMode);
            await _native.PlayCampathAsync(path, mode, EndBehavior).ConfigureAwait(true);
            Status = $"Playing {InterpolationMode.ToString().ToUpperInvariant()} path {mode} across {Keyframes.Count} keyframes.";
        }
        catch (Exception ex)
        {
            _log.Warn($"Campath play failed: {ex.Message}");
            Status = ex.Message;
        }
        finally
        {
            _operationInFlight = false;
            RaiseNativeProperties();
        }
    }

    public async Task GoToAsync()
    {
        if (SelectedKeyframe is not { } selected)
            return;
        _operationInFlight = true;
        RaiseCommandStates();
        try
        {
            await _native.GoToKeyframeAsync(selected).ConfigureAwait(true);
            Status = $"At keyframe tick {selected.DemoTick}; camera held by MVM.";
        }
        catch (Exception ex)
        {
            _log.Warn($"Campath Go To failed: {ex.Message}");
            Status = ex.Message;
        }
        finally
        {
            _operationInFlight = false;
            RaiseNativeProperties();
        }
    }

    public async Task StopAsync()
    {
        _operationInFlight = true;
        RaiseCommandStates();
        try
        {
            await _native.StopCampathAsync().ConfigureAwait(true);
            Status = "Camera ownership released; replay playback state unchanged.";
        }
        catch (Exception ex)
        {
            _log.Warn($"Campath stop failed: {ex.Message}");
            Status = "Camera release failed; native heartbeat will fail closed.";
        }
        finally
        {
            _operationInFlight = false;
            RaiseNativeProperties();
        }
    }

    private void DeleteSelected()
    {
        if (SelectedKeyframe is not { } selected || !CanEditPath)
            return;
        PushHistory();
        Keyframes.Remove(selected);
        SelectedKeyframe = null;
        Status = "Keyframe deleted.";
        OnCollectionChanged();
        Autosave();
        OnEditorStateChanged();
    }

    private void Clear()
    {
        if (!CanEditPath)
            return;
        if (DateTime.UtcNow > _clearConfirmationDeadline)
        {
            _clearConfirmationDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            Status = "Press CLEAR again within 5 seconds to remove every keyframe.";
            return;
        }
        _clearConfirmationDeadline = default;
        PushHistory();
        Keyframes.Clear();
        SelectedKeyframe = null;
        Status = "Campath cleared.";
        OnCollectionChanged();
        Autosave();
        OnEditorStateChanged();
    }

    public void SelectKeyframe(int index)
    {
        if (index >= 0 && index < Keyframes.Count)
            SelectedKeyframe = Keyframes[index];
    }

    public void DeleteKeyframe(int index)
    {
        if (!CanEditPath || Keyframes.Count == 0)
            return;
        var target = index >= 0 && index < Keyframes.Count
            ? Keyframes[index]
            : SelectedKeyframe ?? Keyframes[^1];
        SelectedKeyframe = target;
        DeleteSelected();
    }

    public void RequestClear()
    {
        if (CanEditPath)
            Clear();
    }

    public IReadOnlyList<CampathKeyframe> GetKeyframeSnapshot() => Keyframes.ToArray();

    public void SaveCurrent()
    {
        if (CanEditPath)
            SaveCore(true);
    }

    public void LoadNextMatching()
    {
        if (!CanEditPath)
            return;
        var replay = CurrentReplayIdentifier();
        if (replay is null)
        {
            Status = "A confirmed replay is required before loading a Campath.";
            return;
        }
        RefreshDocuments();
        var matches = SavedCampaths.Where(document => document.ReplayIdentifier.Matches(replay)).ToArray();
        if (matches.Length == 0)
        {
            Status = "No saved Campath matches this replay.";
            return;
        }
        var current = Array.FindIndex(matches, document =>
            string.Equals(document.FilePath, _currentFilePath, StringComparison.OrdinalIgnoreCase));
        SelectedDocument = matches[(current + 1) % matches.Length];
        Load();
    }

    private EditState CaptureEditState() => new(
        Keyframes.ToArray(),
        SelectedKeyframe?.DemoTick,
        InterpolationMode,
        EasingMode,
        EndBehavior);

    private void PushHistory()
    {
        if (_restoringHistory || _suppressAutosave)
            return;
        _undo.Push(CaptureEditState());
        if (_undo.Count > 64)
        {
            var retained = _undo.Take(64).Reverse().ToArray();
            _undo.Clear();
            foreach (var state in retained)
                _undo.Push(state);
        }
        _redo.Clear();
        RaiseCommandStates();
    }

    private void Undo()
    {
        if (_undo.Count == 0 || !CanEditPath)
            return;
        _redo.Push(CaptureEditState());
        RestoreEditState(_undo.Pop(), "Campath edit undone.");
    }

    private void Redo()
    {
        if (_redo.Count == 0 || !CanEditPath)
            return;
        _undo.Push(CaptureEditState());
        RestoreEditState(_redo.Pop(), "Campath edit redone.");
    }

    private void RestoreEditState(EditState state, string message)
    {
        _restoringHistory = true;
        try
        {
            Keyframes.Clear();
            foreach (var keyframe in state.Keyframes)
                Keyframes.Add(keyframe);
            _interpolationMode = state.Interpolation;
            _easingMode = state.Easing;
            _endBehavior = state.EndBehavior;
            _selectedKeyframe = state.SelectedTick is { } tick
                ? Keyframes.FirstOrDefault(keyframe => keyframe.DemoTick == tick)
                : null;
            OnPropertyChanged(nameof(InterpolationMode));
            OnPropertyChanged(nameof(EasingMode));
            OnPropertyChanged(nameof(EndBehavior));
            OnPropertyChanged(nameof(SelectedKeyframe));
            Status = message;
            OnCollectionChanged();
            Autosave();
            OnEditorStateChanged();
        }
        finally
        {
            _restoringHistory = false;
        }
    }

    private void Save()
        => SaveCore(true);

    private void SaveCore(bool reportStatus, bool forceNewFile = false)
    {
        try
        {
            if (CurrentReplayIdentifier() is null)
                throw new InvalidOperationException("A confirmed replay is required before saving a Campath.");
            _currentFilePath = _store.Save(CreateProject(), forceNewFile ? null : _currentFilePath);
            _isDraft = false;
            _store.DeleteDraft();
            _settings.SelectedCampathPath = _currentFilePath;
            _settings.Save();
            if (reportStatus)
                Status = $"Saved {Path.GetFileName(_currentFilePath)}.";
            RefreshDocuments();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            _log.Warn($"Campath save failed: {ex.Message}");
            Status = ex.Message;
        }
    }

    private void Autosave()
    {
        if (_suppressAutosave || CurrentReplayIdentifier() is null ||
            (Keyframes.Count == 0 && _currentFilePath is null && !_isDraft))
            return;
        if (SessionState == CampathSessionState.DraftPath)
        {
            // Drafts autosave only to the recovery file — never to a named path,
            // never to the recent/restore-last setting.
            try
            {
                _store.SaveDraft(CreateProject());
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                _log.Warn($"Campath draft autosave failed: {ex.Message}");
            }
            return;
        }
        SaveCore(false);
    }

    private CampathProject CreateProject()
    {
        var replay = CurrentReplayIdentifier() ??
            throw new InvalidOperationException("A confirmed replay is required before saving a Campath.");
        return new CampathProject
        {
            Name = PathName,
            ReplayIdentifier = replay,
            InterpolationMode = InterpolationMode,
            EasingMode = EasingMode,
            EndBehavior = EndBehavior,
            Keyframes = Keyframes.OrderBy(keyframe => keyframe.DemoTick).ToList(),
        };
    }

    private void Load()
    {
        if (SelectedDocument is not { } document || !CanEditPath)
            return;
        LoadDocument(document);
    }

    /// <summary>Explicitly loads a saved document (or the recovery draft when IsDraft is set).</summary>
    public void LoadDocument(CampathDocumentInfo document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!CanEditPath)
            return;
        var replay = CurrentReplayIdentifier();
        if (replay is null)
        {
            Status = "A confirmed replay is required before loading a Campath.";
            return;
        }
        try
        {
            var project = document.IsDraft
                ? _store.TryLoadDraft(replay) ??
                  throw new InvalidDataException("The unsaved draft is no longer available for this replay.")
                : _store.Load(document.FilePath, replay);
            _suppressAutosave = true;
            Keyframes.Clear();
            foreach (var keyframe in project.Keyframes.OrderBy(keyframe => keyframe.DemoTick))
                Keyframes.Add(keyframe);
            PathName = project.Name;
            InterpolationMode = project.InterpolationMode;
            EasingMode = project.EasingMode;
            EndBehavior = project.EndBehavior;
            if (document.IsDraft)
            {
                // Loading the draft resumes the unsaved draft session.
                _currentFilePath = null;
                _isDraft = true;
                _store.MarkDraftActive();
                RecoveryAvailable = false;
            }
            else
            {
                _currentFilePath = document.FilePath;
                _isDraft = false;
                _store.DeleteDraft();
                _settings.SelectedCampathPath = _currentFilePath;
                _settings.Save();
            }
            SelectedKeyframe = Keyframes.FirstOrDefault();
            ClearHistory();
            Status = document.IsDraft
                ? $"Recovered draft {project.Name} ({Keyframes.Count} keyframes, unsaved)."
                : $"Loaded {project.Name} ({Keyframes.Count} keyframes).";
            OnCollectionChanged();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            _log.Warn($"Campath load failed: {ex.Message}");
            Status = ex.Message;
        }
        finally
        {
            _suppressAutosave = false;
        }
    }

    /// <summary>Creates a clean, empty draft. Never loads old keyframes.</summary>
    public void NewPath()
    {
        if (!CanEditPath)
            return;
        _suppressAutosave = true;
        Keyframes.Clear();
        SelectedKeyframe = null;
        _currentFilePath = null;
        _pathName = "Untitled Path";
        OnPropertyChanged(nameof(PathName));
        _suppressAutosave = false;
        _isDraft = true;
        _store.DeleteDraft();
        ClearHistory();
        Status = "New draft created. Move the camera and press Mouse3 to add keyframes.";
        OnCollectionChanged();
        OnEditorStateChanged();
    }

    /// <summary>Saves the draft under a new explicit name, creating a new file.</summary>
    public void SaveAs(string? name)
    {
        if (!CanEditPath || CurrentReplayIdentifier() is null)
        {
            Status = "A confirmed replay is required before saving a Campath.";
            return;
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            _pathName = name.Trim();
            OnPropertyChanged(nameof(PathName));
        }
        SaveCore(true, forceNewFile: true);
        OnEditorStateChanged();
    }

    /// <summary>Unloads the current path and returns to NoPath. Saved files stay on disk.</summary>
    public void ClosePath()
    {
        if (!CanEditPath)
            return;
        _suppressAutosave = true;
        Keyframes.Clear();
        SelectedKeyframe = null;
        _currentFilePath = null;
        _pathName = "Untitled Path";
        OnPropertyChanged(nameof(PathName));
        _suppressAutosave = false;
        _isDraft = false;
        _store.DeleteDraft();
        _settings.SelectedCampathPath = string.Empty;
        _settings.Save();
        ClearHistory();
        Status = "Path closed.";
        OnCollectionChanged();
        OnEditorStateChanged();
    }

    /// <summary>Recovers the abandoned on-disk draft after an abnormal shutdown.</summary>
    public void RecoverDraft()
    {
        if (!CanEditPath)
            return;
        var replay = CurrentReplayIdentifier();
        if (replay is null)
        {
            Status = "A confirmed replay is required before recovering a Campath draft.";
            return;
        }
        RecoveryAvailable = false;
        LoadDocument(new CampathDocumentInfo(
            "Untitled Path", _store.DraftFilePath, replay, IsDraft: true));
    }

    /// <summary>Permanently discards the abandoned on-disk draft.</summary>
    public void DiscardDraft()
    {
        _store.DeleteDraft();
        RecoveryAvailable = false;
        Status = "Unsaved draft discarded.";
        OnEditorStateChanged();
    }

    /// <summary>The picker list: recovery draft first (when present), then saved paths newest first.</summary>
    public IReadOnlyList<CampathDocumentInfo> GetDocuments()
    {
        var replay = CurrentReplayIdentifier();
        var documents = new List<CampathDocumentInfo>();
        if (replay is not null && _store.TryLoadDraft(replay) is { } draft &&
            SessionState != CampathSessionState.DraftPath)
        {
            documents.Add(new CampathDocumentInfo(
                $"{draft.Name} (unsaved draft)",
                _store.DraftFilePath,
                draft.ReplayIdentifier,
                draft.Keyframes.Count,
                File.GetLastWriteTimeUtc(_store.DraftFilePath),
                IsDraft: true));
        }
        documents.AddRange(SavedCampaths
            .OrderByDescending(document => document.ModifiedUtc ?? DateTime.MinValue));
        return documents;
    }

    /// <summary>Loads the picker-list entry at <paramref name="index"/> (same order as GetDocuments).</summary>
    public void LoadPathByIndex(int index)
    {
        var documents = GetDocuments();
        if (index >= 0 && index < documents.Count)
            LoadDocument(documents[index]);
    }

    /// <summary>Clean application shutdown: the draft stays recoverable but is not flagged as crashed.</summary>
    public void Shutdown() => _store.CompleteCleanShutdown();

    private void RefreshDocuments()
    {
        SavedCampaths.Clear();
        foreach (var document in _store.List())
            SavedCampaths.Add(document);
    }

    private CampathReplayIdentifier? CurrentReplayIdentifier()
    {
        var state = _controller.State;
        return !state.Connected || string.IsNullOrWhiteSpace(state.ReplayName) || state.CurrentTick is null
            ? null
            : new CampathReplayIdentifier(state.ReplayName, state.TotalTicks);
    }

    private void StopForEditIfNeeded()
    {
        if (PathCameraOwned)
            _ = StopAsync();
    }

    private void OnNativeStatusChanged(object? sender, EventArgs e) => SetOnUi(() =>
    {
        RaiseNativeProperties();
    });

    private void OnCampathStateChanged(object? sender, CampathPlaybackStatus playback) => SetOnUi(() =>
    {
        Status = playback.State switch
        {
            CampathPlaybackState.Completed => playback.Detail,
            CampathPlaybackState.Stopped when Status.StartsWith("Playing", StringComparison.OrdinalIgnoreCase) =>
                "Campath completed; camera ownership released.",
            CampathPlaybackState.Error => playback.Detail,
            _ => Status,
        };
        RaiseNativeProperties();
        OnEditorStateChanged();
    });

    private void OnReplayStateChanged(object? sender, ReplayState state) => SetOnUi(() =>
    {
        var currentReplay = CurrentReplayIdentifier();
        if (currentReplay is not null &&
            (_lastReplayIdentifier is null || !_lastReplayIdentifier.Matches(currentReplay)))
        {
            var changedBetweenReplays = _lastReplayIdentifier is not null;
            _lastReplayIdentifier = currentReplay;
            if (changedBetweenReplays)
            {
                _suppressAutosave = true;
                Keyframes.Clear();
                SelectedKeyframe = null;
                _currentFilePath = null;
                _isDraft = false;
                _pathName = "Untitled Path";
                OnPropertyChanged(nameof(PathName));
                _suppressAutosave = false;
                ClearHistory();
                RecoveryAvailable = false;
                Status = "Replay changed; load a matching Campath or start a new path.";
                OnCollectionChanged();
            }
            RefreshDocuments();
            if (!changedBetweenReplays)
                EvaluateStartupWorkspace(currentReplay);
        }
        else if (currentReplay is not null && _lastReplayIdentifier is not null &&
                 _lastReplayIdentifier.TotalTicks is null && currentReplay.TotalTicks is not null)
        {
            _lastReplayIdentifier = currentReplay;
        }
        RaiseCommandStates();
    });

    private void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        RaiseCommandStates();
    }

    /// <summary>
    /// Runs once when a replay is first confirmed. The default workspace is
    /// always clean: no path is loaded unless the user opted into
    /// "Restore last workspace". An abandoned draft from an abnormal shutdown is
    /// only surfaced as an explicit recovery choice, never silently restored.
    /// </summary>
    private void EvaluateStartupWorkspace(CampathReplayIdentifier replay)
    {
        if (Keyframes.Count > 0 || SessionState != CampathSessionState.NoPath)
            return;
        if (_settings.RestoreLastWorkspace)
        {
            TryRestoreSelectedCampath();
            return;
        }
        RecoveryAvailable = _store.HasAbandonedDraft && _store.TryLoadDraft(replay) is not null;
        if (RecoveryAvailable)
            Status = "An unsaved Campath draft from a previous session can be recovered.";
    }

    private void TryRestoreSelectedCampath()
    {
        if (Keyframes.Count > 0 || string.IsNullOrWhiteSpace(_settings.SelectedCampathPath))
            return;
        var document = SavedCampaths.FirstOrDefault(item =>
            string.Equals(item.FilePath, _settings.SelectedCampathPath, StringComparison.OrdinalIgnoreCase));
        var replay = CurrentReplayIdentifier();
        if (document is null || replay is null || !document.ReplayIdentifier.Matches(replay))
            return;
        SelectedDocument = document;
        Load();
    }

    private void RaiseNativeProperties()
    {
        OnPropertyChanged(nameof(NativeState));
        OnPropertyChanged(nameof(NativeMessage));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(CameraOwned));
        OnPropertyChanged(nameof(PathCameraOwned));
        OnPropertyChanged(nameof(CanEditPath));
        RaiseCommandStates();
    }

    private void OnCollectionChanged()
    {
        OnPropertyChanged(nameof(KeyframeCount));
        RaiseCommandStates();
        OnEditorStateChanged();
    }

    private void RaiseCommandStates()
    {
        (AddCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GoToCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PlayCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PlayCurrentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UndoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RedoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LoadCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void SetOnUi(Action action)
    {
        if (_dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(action);
        else
            action();
    }

    private void OnEditorStateChanged() => EditorStateChanged?.Invoke(this, EventArgs.Empty);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
