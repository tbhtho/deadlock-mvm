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

/// <summary>External Director view model for Camera v0.1 discovery.</summary>
public sealed class CameraViewModel : INotifyPropertyChanged
{
    private readonly ICameraService _camera;
    private readonly ReplayController _controller;
    private readonly ILogService _log;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private string _status = "Camera discovery ready";
    private string _positionText = "Unavailable";
    private string _rotationText = "Unavailable";
    private string _baseFovText = "Unavailable";
    private string _heroFovText = "Unavailable";
    private string _activeFovText = "Unavailable";
    private string _playerTarget = "1";
    private string _baseFovInput = "75";
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
        InEyeCommand = new RelayCommand(() => Send("in-eye POV", _camera.SelectInEye), () => IsConnected);
        ChaseCommand = new RelayCommand(() => Send("chase POV", _camera.SelectChase), () => IsConnected);
        SetBaseFovCommand = new RelayCommand(SetBaseFov, () => IsConnected);

        _controller.StateChanged += OnStateChanged;
    }

    public CameraCapabilities Capabilities => _camera.Capabilities;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    public string RotationText
    {
        get => _rotationText;
        private set => SetProperty(ref _rotationText, value);
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

    public string BaseFovInput
    {
        get => _baseFovInput;
        set => SetProperty(ref _baseFovInput, value);
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
    public ICommand InEyeCommand { get; }
    public ICommand ChaseCommand { get; }
    public ICommand SetBaseFovCommand { get; }

    public string RestoreLimitation => Capabilities.Limitation;

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
            PositionText = $"{transform.X:0.000000}  {transform.Y:0.000000}  {transform.Z:0.000000}";
            RotationText = $"Pitch {transform.Pitch:0.000000}  Yaw {transform.Yaw:0.000000}  Roll {transform.Roll:0.000000}";
        }

        BaseFovText = state.BaseFov is { } baseFov ? $"{baseFov:0.##} (base)" : "Unavailable";
        HeroFovText = state.HeroFov is { } heroFov ? $"{heroFov:0.##} (hero follow)" : "Unavailable";
        ActiveFovText = state.ActiveFov is { } activeFov ? $"{activeFov:0.##}" : "Unavailable";
        Status = "Read from engine getpos";
    }

    private void SetBaseFov()
    {
        if (!double.TryParse(BaseFovInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var fov))
        {
            Status = "Enter a numeric base FOV.";
            return;
        }

        Send("base FOV", () => _camera.SetBaseFov(fov));
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
        (InEyeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChaseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SetBaseFovCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
