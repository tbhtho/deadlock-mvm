using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Input;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;
using DeadlockMVM.Launcher.ViewModels;

namespace DeadlockMVM.Launcher.Director;

/// <summary>
/// External Director view model for the Camera page. The active transform is
/// live: a CameraStatePoller polls the engine while the page is visible and the
/// replay is connected, so the values track the camera without a read button.
/// </summary>
public sealed class CameraViewModel : INotifyPropertyChanged
{
    private readonly ICameraService _camera;
    private readonly ReplayController _controller;
    private readonly ILogService _log;
    private readonly CameraStatePoller _poller;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private string _status = "Camera ready";
    private string _posXText = "—";
    private string _posYText = "—";
    private string _posZText = "—";
    private string _pitchText = "—";
    private string _yawText = "—";
    private string _rollText = "—";
    private string _playerSlotText = "—";
    private bool _isFreeRoamActive;
    private bool _isInEyeActive;
    private bool _isChaseActive;
    private bool _isConnected;
    private bool _isPageActive;
    private string _activeFovText = "Unavailable";
    private string _fovInputText = string.Empty;
    private double _fovSliderValue = 90;
    private bool _suppressFovEvents;
    private bool _fovEditing;
    private long _lastFovSliderWriteUtcTicks;
    private bool _cameraOpInFlight;

    private const double SliderMinFov = 10;
    private const double SliderMaxFov = 120;
    private static readonly TimeSpan SliderWriteInterval = TimeSpan.FromMilliseconds(66);

    public CameraViewModel(ICameraService camera, ReplayController controller, ILogService log)
    {
        _camera = camera;
        _controller = controller;
        _log = log;
        _dispatcher = System.Windows.Threading.Dispatcher.FromThread(System.Threading.Thread.CurrentThread);
        _poller = new CameraStatePoller(_camera);
        _poller.StateUpdated += OnPolledState;

        FreeRoamCommand = new RelayCommand(() => _ = FreeRoamAsync(), () => IsConnected);
        PrevPlayerCommand = new RelayCommand(() => Send("previous player", _camera.SelectPrevPlayer), () => IsConnected);
        NextPlayerCommand = new RelayCommand(() => Send("next player", _camera.SelectNextPlayer), () => IsConnected);
        InEyeCommand = new RelayCommand(() => Send("in-eye POV", _camera.SelectInEye), () => IsConnected);
        ChaseCommand = new RelayCommand(() => Send("chase POV", _camera.SelectChase), () => IsConnected);
        SetFovPresetCommand = new RelayCommand<string>(
            preset => _ = ApplyFovAsync(double.Parse(preset!, System.Globalization.CultureInfo.InvariantCulture)),
            () => IsConnected && Capabilities.CanWriteActiveFov);
        SaveCameraCommand = new RelayCommand(
            () => _ = SaveCameraAsync(),
            () => IsConnected && Capabilities.CanSaveRestore && !_cameraOpInFlight);
        RestoreCameraCommand = new RelayCommand(
            () => _ = RestoreCameraAsync(),
            () => IsConnected && Capabilities.CanSaveRestore && _camera.SavedShot is not null && !_cameraOpInFlight);

        _camera.SelectionChanged += OnSelectionChanged;
        _camera.CapabilitiesChanged += OnCapabilitiesChanged;
        _controller.StateChanged += OnStateChanged;
    }

    public CameraCapabilities Capabilities => _camera.Capabilities;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string PosXText
    {
        get => _posXText;
        private set => SetProperty(ref _posXText, value);
    }

    public string PosYText
    {
        get => _posYText;
        private set => SetProperty(ref _posYText, value);
    }

    public string PosZText
    {
        get => _posZText;
        private set => SetProperty(ref _posZText, value);
    }

    public string PitchText
    {
        get => _pitchText;
        private set => SetProperty(ref _pitchText, value);
    }

    public string YawText
    {
        get => _yawText;
        private set => SetProperty(ref _yawText, value);
    }

    public string RollText
    {
        get => _rollText;
        private set => SetProperty(ref _rollText, value);
    }

    /// <summary>Tracked spectator target ("Player 4"), or — when not yet known.</summary>
    public string PlayerSlotText
    {
        get => _playerSlotText;
        private set => SetProperty(ref _playerSlotText, value);
    }

    public bool IsFreeRoamActive
    {
        get => _isFreeRoamActive;
        private set => SetProperty(ref _isFreeRoamActive, value);
    }

    public bool IsInEyeActive
    {
        get => _isInEyeActive;
        private set => SetProperty(ref _isInEyeActive, value);
    }

    public bool IsChaseActive
    {
        get => _isChaseActive;
        private set => SetProperty(ref _isChaseActive, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value))
                return;

            RaiseCommandStates();
            UpdatePolling();
        }
    }

    public ICommand FreeRoamCommand { get; }
    public ICommand PrevPlayerCommand { get; }
    public ICommand NextPlayerCommand { get; }
    public ICommand InEyeCommand { get; }
    public ICommand ChaseCommand { get; }
    public ICommand SetFovPresetCommand { get; }
    public ICommand SaveCameraCommand { get; }
    public ICommand RestoreCameraCommand { get; }

    /// <summary>Live active-camera FOV ("40°"), or Unavailable when not engine-backed.</summary>
    public string ActiveFovText
    {
        get => _activeFovText;
        private set => SetProperty(ref _activeFovText, value);
    }

    /// <summary>Exact FOV entry text. Commits on Enter / focus loss, never mid-typing.</summary>
    public string FovInputText
    {
        get => _fovInputText;
        set => SetProperty(ref _fovInputText, value);
    }

    public double FovSliderValue
    {
        get => _fovSliderValue;
        private set => SetProperty(ref _fovSliderValue, value);
    }

    public bool IsFovEditorVisible => Capabilities.CanWriteActiveFov;

    public bool IsSaveRestoreVisible => Capabilities.CanSaveRestore;

    public string RestoreLimitation
    {
        get
        {
            if (Capabilities.CanSaveRestore)
                return string.Empty;
            var limitation = Capabilities.Limitation;
            return limitation.Length > 0 ? limitation : "Unavailable.";
        }
    }

    /// <summary>TextBox commit (Enter key / focus loss): parse and apply exact FOV.</summary>
    public void CommitFovInput()
    {
        _fovEditing = false;
        var text = FovInputText.Trim().TrimEnd('°').Trim();
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var fov) ||
            !double.IsFinite(fov))
        {
            Status = "FOV must be a number.";
            SyncFovFromEngine();
            return;
        }

        _ = ApplyFovAsync(fov);
    }

    /// <summary>Slider movement: throttled live writes; the exact value lands on release.</summary>
    public void OnFovSliderChanged(double value)
    {
        if (_suppressFovEvents)
            return;

        FovSliderValue = value;
        if (!Capabilities.CanWriteActiveFov)
            return;

        var now = DateTime.UtcNow.Ticks;
        if (now - _lastFovSliderWriteUtcTicks < SliderWriteInterval.Ticks)
            return;

        _lastFovSliderWriteUtcTicks = now;
        _ = ApplyFovAsync(value);
    }

    /// <summary>Called when a slider drag completes — commits the exact final value.</summary>
    public void CommitFovSlider()
    {
        if (Capabilities.CanWriteActiveFov)
            _ = ApplyFovAsync(FovSliderValue);
    }

    /// <summary>While the user edits (text focus / slider drag), poll sync must not fight them.</summary>
    public void SetFovEditing(bool editing) => _fovEditing = editing;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Called by the Director when the Camera page becomes visible/hidden.</summary>
    public void SetPageActive(bool isActive)
    {
        _isPageActive = isActive;
        UpdatePolling();
    }

    private void UpdatePolling()
    {
        if (_isPageActive && IsConnected)
        {
            _poller.Start();
        }
        else
        {
            _poller.Stop();
            if (!IsConnected)
                ClearTransform();
        }
    }

    private void OnPolledState(object? sender, CameraState state)
        => SetOnUi(() => ApplyTransform(state));

    private void ApplyTransform(CameraState state)
    {
        if (state.ActiveTransform is { } transform)
        {
            PosXText = $"{transform.X:0.000}";
            PosYText = $"{transform.Y:0.000}";
            PosZText = $"{transform.Z:0.000}";
            PitchText = $"{transform.Pitch:0.000}";
            YawText = $"{transform.Yaw:0.000}";
            RollText = $"{transform.Roll:0.000}";
        }

        if (state.ActiveFov is { } fov)
        {
            ActiveFovText = $"{fov:0.0#}°";
            if (!_fovEditing)
            {
                _suppressFovEvents = true;
                FovInputText = $"{fov:0.0#}";
                FovSliderValue = Math.Clamp(fov, SliderMinFov, SliderMaxFov);
                _suppressFovEvents = false;
            }
        }
        else
        {
            ActiveFovText = "Unavailable";
        }
    }

    private void ClearTransform()
    {
        PosXText = "—";
        PosYText = "—";
        PosZText = "—";
        PitchText = "—";
        YawText = "—";
        RollText = "—";
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
        => SetOnUi(ApplySelection);

    private void OnCapabilitiesChanged(object? sender, EventArgs e)
        => SetOnUi(() =>
        {
            OnPropertyChanged(nameof(Capabilities));
            OnPropertyChanged(nameof(IsFovEditorVisible));
            OnPropertyChanged(nameof(IsSaveRestoreVisible));
            OnPropertyChanged(nameof(RestoreLimitation));
            RaiseCommandStates();
        });

    private async Task ApplyFovAsync(double fov)
    {
        try
        {
            var ok = await _camera.SetActiveFovAsync(fov).ConfigureAwait(true);
            Status = ok ? $"FOV set to {fov:0.0#}°." : "FOV control unavailable.";
            if (ok)
            {
                _suppressFovEvents = true;
                FovInputText = $"{fov:0.0#}";
                _suppressFovEvents = false;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Camera FOV write failed: {ex.Message}");
            Status = "FOV control unavailable.";
        }
    }

    private void SyncFovFromEngine()
    {
        _suppressFovEvents = true;
        var text = ActiveFovText.TrimEnd('°');
        FovInputText = text == "Unavailable" ? string.Empty : text;
        _suppressFovEvents = false;
    }

    private async Task SaveCameraAsync()
    {
        _cameraOpInFlight = true;
        RaiseCommandStates();
        try
        {
            var shot = await _camera.SaveCameraAsync().ConfigureAwait(true);
            Status = shot is null ? "Save unavailable." : "Camera saved.";
        }
        catch (Exception ex)
        {
            _log.Warn($"Camera save failed: {ex.Message}");
            Status = "Save unavailable.";
        }
        finally
        {
            _cameraOpInFlight = false;
            RaiseCommandStates();
        }
    }

    private async Task RestoreCameraAsync()
    {
        _cameraOpInFlight = true;
        RaiseCommandStates();
        try
        {
            var ok = await _camera.RestoreCameraAsync().ConfigureAwait(true);
            Status = ok ? "Camera restored." : "Restore could not be verified.";
        }
        catch (Exception ex)
        {
            _log.Warn($"Camera restore failed: {ex.Message}");
            Status = "Restore unavailable.";
        }
        finally
        {
            _cameraOpInFlight = false;
            RaiseCommandStates();
        }
    }

    private void ApplySelection()
    {
        var selection = _camera.Selection;
        PlayerSlotText = selection.PlayerSlot is { } slot ? $"Player {slot}" : "—";
        IsFreeRoamActive = selection.Mode == SpecCameraMode.FreeRoam;
        IsInEyeActive = selection.Mode == SpecCameraMode.InEye;
        IsChaseActive = selection.Mode == SpecCameraMode.Chase;
    }

    private void Send(string label, Action action)
    {
        try
        {
            action();
            Status = $"Sent {label} command.";
        }
        catch (Exception ex)
        {
            _log.Warn($"Camera {label} failed: {ex.Message}");
            Status = $"{label} unavailable.";
        }
    }

    private async Task FreeRoamAsync()
    {
        try
        {
            await _camera.EnterFreeRoamAsync().ConfigureAwait(true);
            Status = "Sent free roam command.";
        }
        catch (Exception ex)
        {
            _log.Warn($"Camera free roam failed: {ex.Message}");
            Status = "free roam unavailable.";
        }
    }

    private void OnStateChanged(object? sender, ReplayState state)
    {
        SetOnUi(() => IsConnected = state.Connected);
    }

    private void SetOnUi(Action action)
    {
        if (_dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(action);
            return;
        }

        action();
    }

    private void RaiseCommandStates()
    {
        (FreeRoamCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PrevPlayerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextPlayerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (InEyeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChaseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SetFovPresetCommand as RelayCommand<string>)?.RaiseCanExecuteChanged();
        (SaveCameraCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RestoreCameraCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
