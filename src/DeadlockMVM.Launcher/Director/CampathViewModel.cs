using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Native.InProcess;
using DeadlockMVM.Core.Services;
using DeadlockMVM.Launcher.ViewModels;

namespace DeadlockMVM.Launcher.Director;

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
    private readonly IHotkeyService _hotkeys;
    private readonly CampathStore _store;
    private readonly ILogService _log;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private CampathKeyframe? _selectedKeyframe;
    private CampathDocumentInfo? _selectedDocument;
    private string _status = "Fly in Free Roam and add camera keyframes.";
    private string _pathName = "Untitled Campath";
    private string? _currentFilePath;
    private string _addHotkeyDisplay = "—";
    private bool _capturingHotkey;
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
        IHotkeyService hotkeys,
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
        _hotkeys = hotkeys;
        _log = log;
        _store = store ?? new CampathStore();
        _dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);

        Keyframes = [];
        SavedCampaths = [];
        AddCommand = new RelayCommand(() => _ = AddAsync(false), CanAdd);
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
        CaptureHotkeyCommand = new RelayCommand(BeginHotkeyCapture, () => !_capturingHotkey && _hotkeys.IsAvailable);
        ClearHotkeyCommand = new RelayCommand(ClearHotkey, () => _addHotkeyDisplay != "—");

        _native.StatusChanged += OnNativeStatusChanged;
        _native.CampathStateChanged += OnCampathStateChanged;
        _controller.StateChanged += OnReplayStateChanged;
        _hotkeys.BindingTriggered += OnHotkeyTriggered;
        _hotkeys.BindingCaptured += OnBindingCaptured;
        RegisterSavedHotkey();
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
            var clean = string.IsNullOrWhiteSpace(value) ? "Untitled Campath" : value.Trim();
            if (PathCameraOwned || !SetProperty(ref _pathName, clean))
                return;
            Autosave();
            OnEditorStateChanged();
        }
    }

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
    public string AddHotkeyDisplay => _capturingHotkey ? "PRESS A KEY…" : _addHotkeyDisplay;

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
    public ICommand CaptureHotkeyCommand { get; }
    public ICommand ClearHotkeyCommand { get; }

    public event EventHandler? EditorStateChanged;

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

    private async Task AddAsync(bool fromHotkey)
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
        AddAuthoritativeKeyframe(keyframe, fromHotkey);
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
        var replaced = CampathKeyframeEditor.Upsert(Keyframes, keyframe);
        SelectedKeyframe = keyframe;
        Status = !replaced
            ? $"Keyframe {Keyframes.IndexOf(keyframe) + 1} added at tick {keyframe.DemoTick}."
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

    private void BeginHotkeyCapture()
    {
        if (!_hotkeys.IsAvailable)
        {
            Status = "Hotkey capture is unavailable; restart the Director to retry input hooks.";
            return;
        }
        _capturingHotkey = true;
        OnPropertyChanged(nameof(AddHotkeyDisplay));
        _hotkeys.BeginCapture(HotkeyAction.CampathAddKeyframe);
        Status = "Press a keyboard key, modifier combination, Mouse3/4/5, or mouse wheel. Escape cancels.";
        RaiseCommandStates();
    }

    private void ClearHotkey()
    {
        if (_capturingHotkey)
        {
            _hotkeys.CancelCapture();
            _capturingHotkey = false;
        }
        _hotkeys.Unregister(HotkeyAction.CampathAddKeyframe);
        _settings.CampathAddHotkey = string.Empty;
        _settings.Save();
        _addHotkeyDisplay = "—";
        OnPropertyChanged(nameof(AddHotkeyDisplay));
        Status = "Add Keyframe hotkey unbound.";
        RaiseCommandStates();
    }

    private void RegisterSavedHotkey()
    {
        if (_settings.InterfaceMode != SmvmInterfaceMode.ExternalDirector)
        {
            _hotkeys.Unregister(HotkeyAction.CampathAddKeyframe);
            _addHotkeyDisplay = "INTERNAL";
            return;
        }
        if (!DeadlockMVM.Core.Models.InputBinding.TryParse(_settings.CampathAddHotkey, out var binding))
        {
            _addHotkeyDisplay = "—";
            return;
        }
        if (_hotkeys.TryRegister(HotkeyAction.CampathAddKeyframe, binding, out var error))
            _addHotkeyDisplay = binding.ToString();
        else
        {
            _addHotkeyDisplay = "—";
            Status = error ?? "Add Keyframe hotkey could not be registered.";
        }
    }

    private void OnHotkeyTriggered(object? sender, HotkeyTriggeredEventArgs args)
    {
        if (_settings.InterfaceMode == SmvmInterfaceMode.ExternalDirector &&
            args.Action == HotkeyAction.CampathAddKeyframe)
            _ = AddAsync(true);
    }

    private void OnBindingCaptured(object? sender, BindingCapturedEventArgs args)
    {
        if (args.Action != HotkeyAction.CampathAddKeyframe)
            return;
        _capturingHotkey = false;
        if (args.Cancelled || args.Binding is not { } binding)
        {
            Status = "Hotkey capture cancelled.";
        }
        else if (_hotkeys.TryRegister(args.Action, binding, out var error))
        {
            _settings.CampathAddHotkey = binding.ToString();
            _settings.Save();
            _addHotkeyDisplay = binding.ToString();
            Status = $"Add Keyframe bound to {_addHotkeyDisplay}.";
        }
        else
        {
            Status = error ?? "That hotkey is unavailable.";
        }
        OnPropertyChanged(nameof(AddHotkeyDisplay));
        RaiseCommandStates();
    }

    private void Save()
        => SaveCore(true);

    private void SaveCore(bool reportStatus)
    {
        try
        {
            if (CurrentReplayIdentifier() is null)
                throw new InvalidOperationException("A confirmed replay is required before saving a Campath.");
            _currentFilePath = _store.Save(CreateProject(), _currentFilePath);
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
            (Keyframes.Count == 0 && _currentFilePath is null))
            return;
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
        var replay = CurrentReplayIdentifier();
        if (replay is null)
        {
            Status = "A confirmed replay is required before loading a Campath.";
            return;
        }
        try
        {
            var project = _store.Load(document.FilePath, replay);
            _suppressAutosave = true;
            Keyframes.Clear();
            foreach (var keyframe in project.Keyframes.OrderBy(keyframe => keyframe.DemoTick))
                Keyframes.Add(keyframe);
            PathName = project.Name;
            InterpolationMode = project.InterpolationMode;
            EasingMode = project.EasingMode;
            EndBehavior = project.EndBehavior;
            _currentFilePath = document.FilePath;
            _settings.SelectedCampathPath = _currentFilePath;
            _settings.Save();
            SelectedKeyframe = Keyframes.FirstOrDefault();
            ClearHistory();
            Status = $"Loaded {project.Name} ({Keyframes.Count} keyframes).";
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
                _pathName = "Untitled Campath";
                OnPropertyChanged(nameof(PathName));
                _suppressAutosave = false;
                ClearHistory();
                Status = "Replay changed; load a matching Campath or start a new path.";
                OnCollectionChanged();
            }
            RefreshDocuments();
            if (!changedBetweenReplays)
                TryRestoreSelectedCampath();
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
        (CaptureHotkeyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearHotkeyCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
