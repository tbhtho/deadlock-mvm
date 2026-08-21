using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using DeadlockMVM.Core;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;
using DeadlockMVM.Launcher.ViewModels;

namespace DeadlockMVM.Launcher.Director;

/// <summary>
/// View model for the Director window. Separates editor-facing state from
/// the Core replay controller so the UI stays testable and clean.
/// </summary>
public sealed class DirectorViewModel : INotifyPropertyChanged
{
    private readonly ICameraService _camera;
    private readonly ReplayController _controller;
    private readonly IAppSettings _settings;
    private readonly ILogService _log;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private string _connectionStatus = "Not connected";
    private string _currentTick = "—";
    private string _totalTicks = "—";
    private string _playbackState = "—";
    private string _timescaleDisplay = "—";
    private string _replayName = "—";
    private bool _isConnected;
    private double _gotoTickValue;
    private NavItem _selectedNav;

    public DirectorViewModel(ICameraService camera, ReplayController controller, IAppSettings settings, ILogService log)
    {
        _camera = camera;
        _controller = controller;
        _settings = settings;
        _log = log;
        _dispatcher = System.Windows.Threading.Dispatcher.FromThread(System.Threading.Thread.CurrentThread);

        NavItems = new ObservableCollection<NavItem>();
        NavItems.Add(new NavItem("REPLAY", true, new RelayCommand(() => SelectedNav = NavItems[0])));
        NavItems.Add(new NavItem("CAMERA", true, new RelayCommand(() => SelectedNav = NavItems[1])));
        NavItems.Add(new NavItem("DOLLY", false, new RelayCommand(() => SelectedNav = NavItems[2])));
        NavItems.Add(new NavItem("VISUALS", false, new RelayCommand(() => SelectedNav = NavItems[3])));
        NavItems.Add(new NavItem("CAPTURE", false, new RelayCommand(() => SelectedNav = NavItems[4])));
        _selectedNav = NavItems[0];
        Camera = new CameraViewModel(_camera, _controller, _log);

        _controller.StateChanged += OnStateChanged;

        PlayCommand = new RelayCommand(() => _controller.Play(), () => _controller.IsConnected);
        PauseCommand = new RelayCommand(() => _controller.Pause(), () => _controller.IsConnected);
        TogglePlaybackCommand = new RelayCommand(() => _controller.TogglePause(), () => _controller.IsConnected);
        TogglePauseCommand = TogglePlaybackCommand;
        StepBackCommand = new RelayCommand(() => _controller.StepBack(), () => _controller.IsConnected);
        StepForwardCommand = new RelayCommand(() => _controller.StepTick(1), () => _controller.IsConnected);
        SetSpeedCommand = new RelayCommand<double>(s => { if (s is { } v) _controller.SetSpeed(v); }, () => _controller.IsConnected);
        GotoTickCommand = new RelayCommand(() =>
        {
            if (_gotoTickValue > 0) _controller.SeekToTick((int)_gotoTickValue);
        }, () => _controller.IsConnected);
        ToggleDemoUiCommand = new RelayCommand(() => _controller.ToggleDemoUi(), () => _controller.IsConnected);
        RefreshStateCommand = new RelayCommand(() =>
        {
            _controller.SendRaw(ReplayCommands.QueryPosition);
        }, () => _controller.IsConnected);
    }

    public ObservableCollection<NavItem> NavItems { get; }

    public ReplayController Controller => _controller;

    public CameraViewModel Camera { get; }

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (!SetProperty(ref _selectedNav, value))
                return;

            OnPropertyChanged(nameof(IsReplayPage));
            OnPropertyChanged(nameof(IsCameraPage));
            OnPropertyChanged(nameof(IsOtherPage));
        }
    }

    public bool IsReplayPage => _selectedNav?.Name == "REPLAY";

    public bool IsCameraPage => _selectedNav?.Name == "CAMERA";

    public bool IsOtherPage => !IsReplayPage && !IsCameraPage;

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string CurrentTick
    {
        get => _currentTick;
        private set => SetProperty(ref _currentTick, value);
    }

    public string TotalTicks
    {
        get => _totalTicks;
        private set => SetProperty(ref _totalTicks, value);
    }

    public string PlaybackState
    {
        get => _playbackState;
        private set
        {
            if (!SetProperty(ref _playbackState, value))
                return;

            OnPropertyChanged(nameof(PlaybackToggleGlyph));
            OnPropertyChanged(nameof(PlaybackToggleToolTip));
        }
    }

    public string PlaybackToggleGlyph => PlaybackState == "PAUSED" ? "▶" : "⏸";

    public string PlaybackToggleToolTip => PlaybackState == "PAUSED" ? "Resume" : "Pause";

    public string TimescaleDisplay
    {
        get => _timescaleDisplay;
        private set => SetProperty(ref _timescaleDisplay, value);
    }

    public string ReplayName
    {
        get => _replayName;
        private set => SetProperty(ref _replayName, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public double GotoTickValue
    {
        get => _gotoTickValue;
        set => SetProperty(ref _gotoTickValue, value);
    }

    public ICommand PlayCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand TogglePlaybackCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand StepBackCommand { get; }
    public ICommand StepForwardCommand { get; }
    public ICommand SetSpeedCommand { get; }
    public ICommand GotoTickCommand { get; }
    public ICommand ToggleDemoUiCommand { get; }
    public ICommand RefreshStateCommand { get; }

    public IReadOnlyList<double> SpeedPresets => ReplayCommands.SpeedPresets;

    private void OnStateChanged(object? sender, ReplayState state)
    {
        // Controller events arrive on transport/timer threads; marshal to the
        // UI thread this view model was created on.
        if (_dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnStateChanged(sender, state));
            return;
        }

        var wasConnected = IsConnected;
        IsConnected = state.Connected;
        ConnectionStatus = state.Connected ? "Connected" : "Not connected";
        RaiseCommandStates();

        if (wasConnected != state.Connected)
            _log.Info(state.Connected
                ? "Director: connected to Deadlock console (VConsole)."
                : "Director: lost connection to Deadlock console.");

        CurrentTick = state.CurrentTick?.ToString() ?? "—";
        TotalTicks = state.TotalTicks?.ToString() ?? "—";

        var previousPlayback = PlaybackState;
        if (state.IsPaused is { } paused)
            PlaybackState = paused ? "PAUSED" : "PLAYING";
        else
            PlaybackState = "—";

        if (previousPlayback != PlaybackState && PlaybackState != "—")
            _log.Info($"Director: playback state {previousPlayback} -> {PlaybackState} at tick {CurrentTick}");

        TimescaleDisplay = state.Timescale is { } t ? $"{t:0.##}x" : "—";

        if (!string.IsNullOrEmpty(state.ReplayName))
            ReplayName = state.ReplayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

    private void RaiseCommandStates()
    {
        (PlayCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (TogglePlaybackCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (TogglePauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StepBackCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StepForwardCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SetSpeedCommand as RelayCommand<double>)?.RaiseCanExecuteChanged();
        (GotoTickCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleDemoUiCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshStateCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed record NavItem(string Name, bool Enabled, ICommand SelectCommand);
