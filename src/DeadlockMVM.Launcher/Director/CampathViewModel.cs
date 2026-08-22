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
    private string? _lastReplayName;

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
        DeleteCommand = new RelayCommand(DeleteSelected, () => SelectedKeyframe is not null && !CameraOwned);
        ClearCommand = new RelayCommand(Clear, () => Keyframes.Count > 0 && !CameraOwned);
        GoToCommand = new RelayCommand(() => _ = GoToAsync(), () => SelectedKeyframe is not null && _native.Available && !_operationInFlight);
        PlayCommand = new RelayCommand(() => _ = PlayAsync(), () => Keyframes.Count >= 2 && _native.Available && !CameraOwned && !_operationInFlight);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => CameraOwned && !_operationInFlight);
        SaveCommand = new RelayCommand(Save, () => Keyframes.Count > 0 && CurrentReplayIdentifier() is not null);
        LoadCommand = new RelayCommand(Load, () => SelectedDocument is not null && !CameraOwned);
        CaptureHotkeyCommand = new RelayCommand(BeginHotkeyCapture, () => !_capturingHotkey);
        ClearHotkeyCommand = new RelayCommand(ClearHotkey, () => _addHotkeyDisplay != "—");

        _native.StatusChanged += OnNativeStatusChanged;
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
                RaiseCommandStates();
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
            if (!SetProperty(ref _pathName, clean))
                return;
            Autosave();
        }
    }

    public CampathInterpolationMode InterpolationMode
    {
        get => _interpolationMode;
        set
        {
            if (!SetProperty(ref _interpolationMode, value))
                return;
            StopForEditIfNeeded();
            Autosave();
        }
    }

    public CampathEasingMode EasingMode
    {
        get => _easingMode;
        set
        {
            if (!SetProperty(ref _easingMode, value))
                return;
            StopForEditIfNeeded();
            Autosave();
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
    public string KeyframeCount => $"{Keyframes.Count} / {CampathPath.MaxKeyframes}";
    public string AddHotkeyDisplay => _capturingHotkey ? "PRESS A KEY…" : _addHotkeyDisplay;

    public ICommand AddCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand GoToCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand CaptureHotkeyCommand { get; }
    public ICommand ClearHotkeyCommand { get; }

    private bool CanAdd() => CanCapture() && Keyframes.Count < CampathPath.MaxKeyframes;

    private bool CanCapture() => CampathCaptureGate.CanCapture(new CampathCaptureAvailability(
        _controller.State.Connected,
        !string.IsNullOrWhiteSpace(_controller.State.ReplayName) && _controller.State.CurrentTick is not null,
        _native.Available,
        _controller.State.CurrentTick is not null,
        _camera.Selection.Mode == SpecCameraMode.FreeRoam,
        CameraOwned,
        _operationInFlight));

    private async Task AddAsync(bool fromHotkey)
    {
        if (!CanAdd())
        {
            Status = CameraOwned
                ? "Add Keyframe is disabled while Campath owns the camera."
                : "Add Keyframe needs an active replay, Free Roam, and a readable native camera.";
            return;
        }

        var keyframe = await CaptureAsync().ConfigureAwait(true);
        if (keyframe is null)
            return;
        var replaced = CampathKeyframeEditor.Upsert(Keyframes, keyframe);
        SelectedKeyframe = keyframe;
        Status = !replaced
            ? $"Keyframe {Keyframes.IndexOf(keyframe) + 1} added at tick {keyframe.DemoTick}."
            : $"Keyframe at tick {keyframe.DemoTick} updated.";
        _log.Info($"Campath: {Status}{(fromHotkey ? " (hotkey)" : string.Empty)}");
        OnCollectionChanged();
        Autosave();
    }

    private async Task UpdateAsync()
    {
        var selected = SelectedKeyframe;
        if (selected is null)
            return;
        var captured = await CaptureAsync().ConfigureAwait(true);
        if (captured is null)
            return;

        var updated = new CampathKeyframe(selected.DemoTick, captured.Camera);
        var index = Keyframes.IndexOf(selected);
        Keyframes[index] = updated;
        SelectedKeyframe = updated;
        Status = $"Updated camera at tick {updated.DemoTick}; timing kept unchanged.";
        Autosave();
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

    private async Task PlayAsync()
    {
        _operationInFlight = true;
        RaiseCommandStates();
        try
        {
            var path = new CampathPath(Keyframes, InterpolationMode, EasingMode);
            await _native.PlayCampathAsync(path).ConfigureAwait(true);
            Status = $"Playing {InterpolationMode.ToString().ToUpperInvariant()} path across {Keyframes.Count} keyframes.";
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

    private async Task GoToAsync()
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

    private async Task StopAsync()
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
        if (SelectedKeyframe is not { } selected)
            return;
        Keyframes.Remove(selected);
        SelectedKeyframe = null;
        Status = "Keyframe deleted.";
        OnCollectionChanged();
        Autosave();
    }

    private void Clear()
    {
        if (DateTime.UtcNow > _clearConfirmationDeadline)
        {
            _clearConfirmationDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            Status = "Press CLEAR again within 5 seconds to remove every keyframe.";
            return;
        }
        _clearConfirmationDeadline = default;
        Keyframes.Clear();
        SelectedKeyframe = null;
        Status = "Campath cleared.";
        OnCollectionChanged();
        Autosave();
    }

    private void BeginHotkeyCapture()
    {
        _capturingHotkey = true;
        OnPropertyChanged(nameof(AddHotkeyDisplay));
        _hotkeys.BeginCapture(HotkeyAction.CampathAddKeyframe);
        Status = "Press a keyboard key, modifier combination, Mouse3, Mouse4, or Mouse5. Escape cancels.";
        RaiseCommandStates();
    }

    private void ClearHotkey()
    {
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
        if (args.Action == HotkeyAction.CampathAddKeyframe)
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
            _currentFilePath = _store.Save(CreateProject(), _currentFilePath);
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

    private CampathProject CreateProject() => new()
    {
        Name = PathName,
        ReplayIdentifier = CurrentReplayIdentifier() ?? new CampathReplayIdentifier("Unknown", null),
        InterpolationMode = InterpolationMode,
        EasingMode = EasingMode,
        Keyframes = Keyframes.OrderBy(keyframe => keyframe.DemoTick).ToList(),
    };

    private void Load()
    {
        if (SelectedDocument is not { } document)
            return;
        try
        {
            var project = _store.Load(document.FilePath, CurrentReplayIdentifier());
            _suppressAutosave = true;
            Keyframes.Clear();
            foreach (var keyframe in project.Keyframes.OrderBy(keyframe => keyframe.DemoTick))
                Keyframes.Add(keyframe);
            PathName = project.Name;
            InterpolationMode = project.InterpolationMode;
            EasingMode = project.EasingMode;
            _currentFilePath = document.FilePath;
            SelectedKeyframe = Keyframes.FirstOrDefault();
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
        return string.IsNullOrWhiteSpace(state.ReplayName)
            ? null
            : new CampathReplayIdentifier(state.ReplayName, state.TotalTicks);
    }

    private void StopForEditIfNeeded()
    {
        if (CameraOwned)
            _ = StopAsync();
    }

    private void OnNativeStatusChanged(object? sender, EventArgs e) => SetOnUi(RaiseNativeProperties);

    private void OnReplayStateChanged(object? sender, ReplayState state) => SetOnUi(() =>
    {
        if (!string.Equals(_lastReplayName, state.ReplayName, StringComparison.OrdinalIgnoreCase))
        {
            var changedBetweenReplays = !string.IsNullOrWhiteSpace(_lastReplayName) &&
                                        !string.IsNullOrWhiteSpace(state.ReplayName);
            _lastReplayName = state.ReplayName;
            if (changedBetweenReplays)
            {
                _suppressAutosave = true;
                Keyframes.Clear();
                SelectedKeyframe = null;
                _currentFilePath = null;
                _pathName = "Untitled Campath";
                OnPropertyChanged(nameof(PathName));
                _suppressAutosave = false;
                Status = "Replay changed; load a matching Campath or start a new path.";
                OnCollectionChanged();
            }
            RefreshDocuments();
        }
        RaiseCommandStates();
    });

    private void RaiseNativeProperties()
    {
        OnPropertyChanged(nameof(NativeState));
        OnPropertyChanged(nameof(NativeMessage));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(CameraOwned));
        RaiseCommandStates();
    }

    private void OnCollectionChanged()
    {
        OnPropertyChanged(nameof(KeyframeCount));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        (AddCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GoToCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PlayCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
