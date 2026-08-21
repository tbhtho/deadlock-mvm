using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;
using DeadlockMVM.Launcher.ViewModels;

namespace DeadlockMVM.Launcher.Director;

/// <summary>External Director view model for the Camera page.</summary>
public sealed class CameraViewModel : INotifyPropertyChanged
{
    private readonly ICameraService _camera;
    private readonly ReplayController _controller;
    private readonly ILogService _log;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private string _status = "Camera ready";
    private string _posXText = "—";
    private string _posYText = "—";
    private string _posZText = "—";
    private string _pitchText = "—";
    private string _yawText = "—";
    private string _rollText = "—";
    private string _baseFovText = "—";
    private string _heroFovText = "—";
    private string _activeFovText = "Unavailable";
    private string _playerTarget = "1";
    private string _gotoX = string.Empty;
    private string _gotoY = string.Empty;
    private string _gotoZ = string.Empty;
    private bool _isConnected;

    public CameraViewModel(ICameraService camera, ReplayController controller, ILogService log)
    {
        _camera = camera;
        _controller = controller;
        _log = log;
        _dispatcher = System.Windows.Threading.Dispatcher.FromThread(System.Threading.Thread.CurrentThread);

        ReadCameraCommand = new RelayCommand(() => _ = ReadCameraAsync(), () => IsConnected);
        FreeRoamCommand = new RelayCommand(() => Send("free roam", _camera.EnterFreeRoam), () => IsConnected);
        SelectPlayerCommand = new RelayCommand(() => Send("player selection", () => _camera.SelectPlayer(PlayerTarget)), () => IsConnected);
        PrevPlayerCommand = new RelayCommand(() => Send("previous player", _camera.SelectPrevPlayer), () => IsConnected);
        NextPlayerCommand = new RelayCommand(() => Send("next player", _camera.SelectNextPlayer), () => IsConnected);
        InEyeCommand = new RelayCommand(() => Send("in-eye POV", _camera.SelectInEye), () => IsConnected);
        ChaseCommand = new RelayCommand(() => Send("chase POV", _camera.SelectChase), () => IsConnected);
        GoToPositionCommand = new RelayCommand(() => _ = GoToPositionAsync(), () => IsConnected && Capabilities.CanWritePosition);

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

    public string BaseFovText
    {
        get => _baseFovText;
        private set => SetProperty(ref _baseFovText, value);
    }

    public string HeroFovText
    {
        get => _heroFovText;
        private set => SetProperty(ref _heroFovText, value);
    }

    public string ActiveFovText
    {
        get => _activeFovText;
        private set => SetProperty(ref _activeFovText, value);
    }

    public string PlayerTarget
    {
        get => _playerTarget;
        set => SetProperty(ref _playerTarget, value);
    }

    public string GotoX
    {
        get => _gotoX;
        set => SetProperty(ref _gotoX, value);
    }

    public string GotoY
    {
        get => _gotoY;
        set => SetProperty(ref _gotoY, value);
    }

    public string GotoZ
    {
        get => _gotoZ;
        set => SetProperty(ref _gotoZ, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value))
                return;

            RaiseCommandStates();
        }
    }

    public ICommand ReadCameraCommand { get; }
    public ICommand FreeRoamCommand { get; }
    public ICommand SelectPlayerCommand { get; }
    public ICommand PrevPlayerCommand { get; }
    public ICommand NextPlayerCommand { get; }
    public ICommand InEyeCommand { get; }
    public ICommand ChaseCommand { get; }
    public ICommand GoToPositionCommand { get; }

    public string RestoreLimitation => Capabilities.CanSaveRestore
        ? string.Empty
        : "Unavailable — camera rotation control is required.";

    public event PropertyChangedEventHandler? PropertyChanged;

    private async Task ReadCameraAsync()
    {
        try
        {
            var state = await _camera.ReadStateAsync().ConfigureAwait(false);
            if (state is null)
            {
                SetOnUi(() => Status = "Camera read unavailable.");
                return;
            }

            SetOnUi(() => ApplyState(state));
        }
        catch (Exception ex)
        {
            _log.Warn($"Camera read failed: {ex.Message}");
            SetOnUi(() => Status = "Camera read unavailable.");
        }
    }

    private void ApplyState(CameraState state)
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

        BaseFovText = state.BaseFov is { } baseFov ? $"{baseFov:0.#}°" : "—";
        HeroFovText = state.HeroFov is { } heroFov ? $"{heroFov:0.#}°" : "—";
        ActiveFovText = "Unavailable";
        Status = "Camera read OK";
    }

    private async Task GoToPositionAsync()
    {
        if (!double.TryParse(GotoX, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(GotoY, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(GotoZ, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            Status = "Enter numeric X/Y/Z.";
            return;
        }

        try
        {
            SetOnUi(() => Status = "Moving camera…");
            var state = await _camera.GoToPositionAsync(x, y, z).ConfigureAwait(false);
            SetOnUi(() =>
            {
                if (state is null)
                {
                    Status = "Position read back unavailable.";
                    return;
                }

                ApplyState(state);
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"Camera go-to-position failed: {ex.Message}");
            SetOnUi(() => Status = "Go to position unavailable.");
        }
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
        (ReadCameraCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (FreeRoamCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SelectPlayerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PrevPlayerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextPlayerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (InEyeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChaseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GoToPositionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private bool SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
