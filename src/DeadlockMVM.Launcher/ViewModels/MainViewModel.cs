using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using DeadlockMVM.Core;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Native.InProcess;
using DeadlockMVM.Core.Services;
using MvmInputBinding = DeadlockMVM.Core.Models.InputBinding;

namespace DeadlockMVM.Launcher.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private static readonly TimeSpan ReplayLaunchDetectionTimeout = TimeSpan.FromMinutes(2);

    private readonly ISteamService _steam;
    private readonly IProcessMonitor _process;
    private readonly IGameLauncher _launcher;
    private readonly IReplayService _replayService;
    private readonly ILogService _log;
    private readonly IAppSettings _settings;
    private readonly DispatcherTimer _timer;

    private string _gameExecutablePath = string.Empty;
    private string _extraArguments = string.Empty;
    private string _commandPreview = string.Empty;
    private string _statusMessage = "Ready";
    private string _steamPath = string.Empty;
    private string _deadlockPath = string.Empty;
    private string _deadlockPathDisplay = "Use Locate Deadlock to select the game.";
    private string _steamStatusText = "Not detected";
    private string _deadlockInstallText = "Deadlock installation not found";
    private string _deadlockStateText = "Not found";
    private string _pathActionText = "Locate Deadlock";
    private bool _steamInstalled;
    private bool _steamRunning;
    private bool _deadlockInstalled;
    private bool _deadlockRunning;
    private bool _isLaunching;
    private bool _isReplaysPage;
    private ReplayInfo? _selectedReplay;
    private string _replayDirectoryPath = string.Empty;
    private string _replayStatusText = "Deadlock installation not found.";
    private int _launchAttemptGeneration;
    private int _launchOperationGeneration;

    public event EventHandler<bool>? LaunchCompleted;
    public event EventHandler<bool>? DeadlockRunningChanged;
    public event EventHandler? LauncherVisibilityPreferenceChanged;

    public MainViewModel(
        ISteamService steam,
        IProcessMonitor process,
        IGameLauncher launcher,
        ILogService log,
        IAppSettings settings,
        IReplayService replayService)
    {
        _steam = steam;
        _process = process;
        _launcher = launcher;
        _replayService = replayService;
        _log = log;
        _settings = settings;
        _extraArguments = MovieModeLaunchPolicy.NormalizeAdditionalArguments(
            settings.ExtraLaunchArguments);

        LaunchCommand = new RelayCommand(Launch, () => CanLaunch);
        StopDeadlockCommand = new RelayCommand(StopDeadlock, () => CanStopDeadlock);
        RefreshCommand = new RelayCommand(Refresh);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        ResetDeadlockPathCommand = new RelayCommand(ResetDeadlockPath);
        RefreshReplaysCommand = new RelayCommand(RefreshReplays);
        OpenReplayFolderCommand = new RelayCommand(OpenReplayFolder);
        ShowHomeCommand = new RelayCommand(() => IsReplaysPage = false);
        ShowReplaysCommand = new RelayCommand(ShowReplaysPage);
        UseSelectedReplayCommand = new RelayCommand(UseSelectedReplay);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => RefreshProcesses();
    }

    public ICommand LaunchCommand { get; }

    public ICommand StopDeadlockCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand OpenLogsCommand { get; }

    public ICommand ResetDeadlockPathCommand { get; }

    public ICommand RefreshReplaysCommand { get; }

    public ICommand OpenReplayFolderCommand { get; }

    public ICommand ShowHomeCommand { get; }

    public ICommand ShowReplaysCommand { get; }

    public ICommand UseSelectedReplayCommand { get; }

    public string SmvmMenuHotkey
    {
        get => _settings.SmvmMenuHotkey;
        set => SetSmvmBinding(nameof(SmvmMenuHotkey), value, 111,
            binding => _settings.SmvmMenuHotkey = binding);
    }

    public string SmvmAddHotkey
    {
        get => _settings.SmvmAddHotkey;
        set => SetSmvmBinding(nameof(SmvmAddHotkey), value, 112,
            binding => _settings.SmvmAddHotkey = binding);
    }

    public string SmvmDeleteHotkey
    {
        get => _settings.SmvmDeleteHotkey;
        set => SetSmvmBinding(nameof(SmvmDeleteHotkey), value, 113,
            binding => _settings.SmvmDeleteHotkey = binding);
    }

    public string SmvmCleanViewHotkey
    {
        get => _settings.SmvmCleanViewHotkey;
        set => SetSmvmBinding(nameof(SmvmCleanViewHotkey), value, 114,
            binding => _settings.SmvmCleanViewHotkey = binding);
    }

    public string SmvmRollLeftHotkey
    {
        get => _settings.SmvmRollLeftHotkey;
        set => SetSmvmBinding(nameof(SmvmRollLeftHotkey), value, 108,
            binding => _settings.SmvmRollLeftHotkey = binding);
    }

    public string SmvmRollRightHotkey
    {
        get => _settings.SmvmRollRightHotkey;
        set => SetSmvmBinding(nameof(SmvmRollRightHotkey), value, 109,
            binding => _settings.SmvmRollRightHotkey = binding);
    }

    public string SmvmRollResetHotkey
    {
        get => _settings.SmvmRollResetHotkey;
        set => SetSmvmBinding(nameof(SmvmRollResetHotkey), value, 110,
            binding => _settings.SmvmRollResetHotkey = binding);
    }

    public string SmvmShowPathHotkey
    {
        get => _settings.SmvmShowPathHotkey;
        set => SetSmvmBinding(nameof(SmvmShowPathHotkey), value, 120,
            binding => _settings.SmvmShowPathHotkey = binding);
    }

    public string SmvmShowCamerasHotkey
    {
        get => _settings.SmvmShowCamerasHotkey;
        set => SetSmvmBinding(nameof(SmvmShowCamerasHotkey), value, 121,
            binding => _settings.SmvmShowCamerasHotkey = binding);
    }

    public string SmvmForwardHotkey
    {
        get => _settings.SmvmForwardHotkey;
        set => SetSmvmBinding(nameof(SmvmForwardHotkey), value, 100, binding => _settings.SmvmForwardHotkey = binding);
    }

    public string SmvmBackHotkey
    {
        get => _settings.SmvmBackHotkey;
        set => SetSmvmBinding(nameof(SmvmBackHotkey), value, 101, binding => _settings.SmvmBackHotkey = binding);
    }

    public string SmvmLeftHotkey
    {
        get => _settings.SmvmLeftHotkey;
        set => SetSmvmBinding(nameof(SmvmLeftHotkey), value, 102, binding => _settings.SmvmLeftHotkey = binding);
    }

    public string SmvmRightHotkey
    {
        get => _settings.SmvmRightHotkey;
        set => SetSmvmBinding(nameof(SmvmRightHotkey), value, 103, binding => _settings.SmvmRightHotkey = binding);
    }

    public string SmvmUpHotkey
    {
        get => _settings.SmvmUpHotkey;
        set => SetSmvmBinding(nameof(SmvmUpHotkey), value, 104, binding => _settings.SmvmUpHotkey = binding);
    }

    public string SmvmDownHotkey
    {
        get => _settings.SmvmDownHotkey;
        set => SetSmvmBinding(nameof(SmvmDownHotkey), value, 105, binding => _settings.SmvmDownHotkey = binding);
    }

    public string SmvmFastHotkey
    {
        get => _settings.SmvmFastHotkey;
        set => SetSmvmBinding(nameof(SmvmFastHotkey), value, 106, binding => _settings.SmvmFastHotkey = binding);
    }

    public string SmvmPrecisionHotkey
    {
        get => _settings.SmvmPrecisionHotkey;
        set => SetSmvmBinding(nameof(SmvmPrecisionHotkey), value, 107, binding => _settings.SmvmPrecisionHotkey = binding);
    }

    public string SmvmPlayStartHotkey
    {
        get => _settings.SmvmPlayStartHotkey;
        set => SetSmvmBinding(nameof(SmvmPlayStartHotkey), value, 115, binding => _settings.SmvmPlayStartHotkey = binding);
    }

    public string SmvmPlayCurrentHotkey
    {
        get => _settings.SmvmPlayCurrentHotkey;
        set => SetSmvmBinding(nameof(SmvmPlayCurrentHotkey), value, 116, binding => _settings.SmvmPlayCurrentHotkey = binding);
    }

    public string SmvmStopHotkey
    {
        get => _settings.SmvmStopHotkey;
        set => SetSmvmBinding(nameof(SmvmStopHotkey), value, 117, binding => _settings.SmvmStopHotkey = binding);
    }

    public string SmvmUndoHotkey
    {
        get => _settings.SmvmUndoHotkey;
        set => SetSmvmBinding(nameof(SmvmUndoHotkey), value, 118, binding => _settings.SmvmUndoHotkey = binding);
    }

    public string SmvmRedoHotkey
    {
        get => _settings.SmvmRedoHotkey;
        set => SetSmvmBinding(nameof(SmvmRedoHotkey), value, 119, binding => _settings.SmvmRedoHotkey = binding);
    }

    public string SmvmRestoreUiHotkey
    {
        get => _settings.SmvmRestoreUiHotkey;
        set => SetSmvmBinding(nameof(SmvmRestoreUiHotkey), value, 122, binding => _settings.SmvmRestoreUiHotkey = binding);
    }

    public string SmvmCycleUiHotkey
    {
        get => _settings.SmvmCycleUiHotkey;
        set => SetSmvmBinding(nameof(SmvmCycleUiHotkey), value, 123, binding => _settings.SmvmCycleUiHotkey = binding);
    }

    public string SmvmToggleFreeCameraHotkey
    {
        get => _settings.SmvmToggleFreeCameraHotkey;
        set => SetSmvmBinding(nameof(SmvmToggleFreeCameraHotkey), value, 124, binding => _settings.SmvmToggleFreeCameraHotkey = binding);
    }

    public string SmvmReplayPauseHotkey
    {
        get => _settings.SmvmReplayPauseHotkey;
        set => SetSmvmBinding(nameof(SmvmReplayPauseHotkey), value, 125, binding => _settings.SmvmReplayPauseHotkey = binding);
    }

    public string SmvmShowLabelsHotkey
    {
        get => _settings.SmvmShowLabelsHotkey;
        set => SetSmvmBinding(nameof(SmvmShowLabelsHotkey), value, 126, binding => _settings.SmvmShowLabelsHotkey = binding);
    }

    public string SmvmStepBackHotkey
    {
        get => _settings.SmvmStepBackHotkey;
        set => SetSmvmBinding(nameof(SmvmStepBackHotkey), value, 127, binding => _settings.SmvmStepBackHotkey = binding);
    }

    public string SmvmStepForwardHotkey
    {
        get => _settings.SmvmStepForwardHotkey;
        set => SetSmvmBinding(nameof(SmvmStepForwardHotkey), value, 128, binding => _settings.SmvmStepForwardHotkey = binding);
    }

    public string SmvmEffectsHotkey
    {
        get => _settings.SmvmEffectsHotkey;
        set => SetSmvmBinding(nameof(SmvmEffectsHotkey), value, 129, binding => _settings.SmvmEffectsHotkey = binding);
    }

    public string SmvmCinematicStartHotkey
    {
        get => _settings.SmvmCinematicStartHotkey;
        set => SetSmvmBinding(nameof(SmvmCinematicStartHotkey), value, 130, binding => _settings.SmvmCinematicStartHotkey = binding);
    }

    public string SmvmPlaybackSlowerHotkey
    {
        get => _settings.SmvmPlaybackSlowerHotkey;
        set => SetSmvmBinding(nameof(SmvmPlaybackSlowerHotkey), value, 131, binding => _settings.SmvmPlaybackSlowerHotkey = binding);
    }

    public string SmvmPlaybackFasterHotkey
    {
        get => _settings.SmvmPlaybackFasterHotkey;
        set => SetSmvmBinding(nameof(SmvmPlaybackFasterHotkey), value, 132, binding => _settings.SmvmPlaybackFasterHotkey = binding);
    }

    public string SmvmCancelHotkey
    {
        get => _settings.SmvmCancelHotkey;
        set => SetSmvmBinding(nameof(SmvmCancelHotkey), value, 133, binding => _settings.SmvmCancelHotkey = binding);
    }

    public string SmvmCameraSlowerHotkey
    {
        get => _settings.SmvmCameraSlowerHotkey;
        set => SetSmvmBinding(nameof(SmvmCameraSlowerHotkey), value, 134, binding => _settings.SmvmCameraSlowerHotkey = binding);
    }

    public string SmvmCameraFasterHotkey
    {
        get => _settings.SmvmCameraFasterHotkey;
        set => SetSmvmBinding(nameof(SmvmCameraFasterHotkey), value, 135, binding => _settings.SmvmCameraFasterHotkey = binding);
    }

    private void SetSmvmBinding(string propertyName, string? value, int slot, Action<string> assign)
    {
        var candidate = value?.Trim() ?? string.Empty;
        MvmInputBinding? parsedCandidate = null;
        if (candidate.Length > 0)
        {
            if (!MvmInputBinding.TryParse(candidate, out var parsed))
            {
                StatusMessage = $"{candidate} is not a valid keyboard, modifier, or Mouse3/4/5 binding.";
                OnPropertyChanged(propertyName);
                return;
            }
            parsedCandidate = parsed;
            if (!SmvmInputCode.IsBindingAllowedForSlot(slot, parsed))
            {
                StatusMessage = $"{propertyName.Replace("Smvm", string.Empty).Replace("Hotkey", string.Empty)} requires a keyboard key.";
                OnPropertyChanged(propertyName);
                return;
            }
            candidate = parsed.ToString();
        }
        var existing = GetSmvmBindings();
        var conflict = existing.FirstOrDefault(pair => pair.Key != propertyName &&
            !(slot == 130 && pair.Key == nameof(SmvmUpHotkey)) &&
            !(slot == 104 && pair.Key == nameof(SmvmCinematicStartHotkey)) && parsedCandidate is { } binding &&
            MvmInputBinding.TryParse(pair.Value, out var existingBinding) && existingBinding == binding);
        if (!string.IsNullOrEmpty(conflict.Key))
        {
            StatusMessage = $"{candidate} is already assigned to {conflict.Key.Replace("Smvm", string.Empty).Replace("Hotkey", string.Empty)}.";
            OnPropertyChanged(propertyName);
            return;
        }
        assign(candidate);
        _settings.Save();
        OnPropertyChanged(propertyName);
        StatusMessage = candidate.Length == 0 ? "SMVM action unbound." : $"SMVM binding set to {candidate}.";
    }

    private Dictionary<string, string> GetSmvmBindings() => new(StringComparer.Ordinal)
    {
        [nameof(SmvmForwardHotkey)] = _settings.SmvmForwardHotkey,
        [nameof(SmvmBackHotkey)] = _settings.SmvmBackHotkey,
        [nameof(SmvmLeftHotkey)] = _settings.SmvmLeftHotkey,
        [nameof(SmvmRightHotkey)] = _settings.SmvmRightHotkey,
        [nameof(SmvmUpHotkey)] = _settings.SmvmUpHotkey,
        [nameof(SmvmDownHotkey)] = _settings.SmvmDownHotkey,
        [nameof(SmvmFastHotkey)] = _settings.SmvmFastHotkey,
        [nameof(SmvmPrecisionHotkey)] = _settings.SmvmPrecisionHotkey,
        [nameof(SmvmRollLeftHotkey)] = _settings.SmvmRollLeftHotkey,
        [nameof(SmvmRollRightHotkey)] = _settings.SmvmRollRightHotkey,
        [nameof(SmvmRollResetHotkey)] = _settings.SmvmRollResetHotkey,
        [nameof(SmvmMenuHotkey)] = _settings.SmvmMenuHotkey,
        [nameof(SmvmAddHotkey)] = _settings.SmvmAddHotkey,
        [nameof(SmvmDeleteHotkey)] = _settings.SmvmDeleteHotkey,
        [nameof(SmvmCleanViewHotkey)] = _settings.SmvmCleanViewHotkey,
        [nameof(SmvmPlayStartHotkey)] = _settings.SmvmPlayStartHotkey,
        [nameof(SmvmPlayCurrentHotkey)] = _settings.SmvmPlayCurrentHotkey,
        [nameof(SmvmStopHotkey)] = _settings.SmvmStopHotkey,
        [nameof(SmvmUndoHotkey)] = _settings.SmvmUndoHotkey,
        [nameof(SmvmRedoHotkey)] = _settings.SmvmRedoHotkey,
        [nameof(SmvmShowPathHotkey)] = _settings.SmvmShowPathHotkey,
        [nameof(SmvmShowCamerasHotkey)] = _settings.SmvmShowCamerasHotkey,
        [nameof(SmvmRestoreUiHotkey)] = _settings.SmvmRestoreUiHotkey,
        [nameof(SmvmCycleUiHotkey)] = _settings.SmvmCycleUiHotkey,
        [nameof(SmvmToggleFreeCameraHotkey)] = _settings.SmvmToggleFreeCameraHotkey,
        [nameof(SmvmReplayPauseHotkey)] = _settings.SmvmReplayPauseHotkey,
        [nameof(SmvmShowLabelsHotkey)] = _settings.SmvmShowLabelsHotkey,
        [nameof(SmvmStepBackHotkey)] = _settings.SmvmStepBackHotkey,
        [nameof(SmvmStepForwardHotkey)] = _settings.SmvmStepForwardHotkey,
        [nameof(SmvmEffectsHotkey)] = _settings.SmvmEffectsHotkey,
        [nameof(SmvmCinematicStartHotkey)] = _settings.SmvmCinematicStartHotkey,
        [nameof(SmvmPlaybackSlowerHotkey)] = _settings.SmvmPlaybackSlowerHotkey,
        [nameof(SmvmPlaybackFasterHotkey)] = _settings.SmvmPlaybackFasterHotkey,
        [nameof(SmvmCancelHotkey)] = _settings.SmvmCancelHotkey,
        [nameof(SmvmCameraSlowerHotkey)] = _settings.SmvmCameraSlowerHotkey,
        [nameof(SmvmCameraFasterHotkey)] = _settings.SmvmCameraFasterHotkey,
    };

    public ObservableCollection<ReplayInfo> Replays { get; } = new();

    public bool SteamInstalled
    {
        get => _steamInstalled;
        private set => SetProperty(ref _steamInstalled, value);
    }

    public bool SteamRunning
    {
        get => _steamRunning;
        private set => SetProperty(ref _steamRunning, value);
    }

    public bool DeadlockInstalled
    {
        get => _deadlockInstalled;
        private set => SetProperty(ref _deadlockInstalled, value);
    }

    public bool DeadlockRunning
    {
        get => _deadlockRunning;
        private set
        {
            if (SetProperty(ref _deadlockRunning, value))
            {
                if (value && IsLaunching)
                {
                    CompleteLaunchOpening();
                    StatusMessage = SelectedReplay is { } replay
                        ? $"Playing {replay.FileName}..."
                        : "Replay is running.";
                    _log.Info("Deadlock process detected; replay launch lock released.");
                }
                DeadlockRunningChanged?.Invoke(this, value);
                OnPropertyChanged(nameof(PlayButtonText));
                NotifyLaunchStateChanged();
            }
        }
    }

    public bool HideLauncherWhileDeadlockRunning
    {
        get => _settings.HideLauncherWhileDeadlockRunning;
        set
        {
            if (_settings.HideLauncherWhileDeadlockRunning == value)
                return;
            _settings.HideLauncherWhileDeadlockRunning = value;
            _settings.Save();
            OnPropertyChanged();
            LauncherVisibilityPreferenceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool LaunchViaSteam
    {
        get => _settings.LaunchViaSteam;
        set
        {
            if (_settings.LaunchViaSteam == value)
                return;
            _settings.LaunchViaSteam = value;
            _settings.Save();
            OnPropertyChanged();
            UpdateCommandPreview();
            _log.Info(value
                ? "Launch mode: through Steam (-applaunch). If the demo is ignored, turn this off for direct launch."
                : "Launch mode: direct project8.exe (recommended for replays; keeps +playdemo on the game command line).");
        }
    }

    public string SteamStatusText
    {
        get => _steamStatusText;
        private set => SetProperty(ref _steamStatusText, value);
    }

    public string DeadlockInstallText
    {
        get => _deadlockInstallText;
        private set => SetProperty(ref _deadlockInstallText, value);
    }

    public string DeadlockStateText
    {
        get => _deadlockStateText;
        private set => SetProperty(ref _deadlockStateText, value);
    }

    public string PathActionText
    {
        get => _pathActionText;
        private set => SetProperty(ref _pathActionText, value);
    }

    public string SteamPath
    {
        get => _steamPath;
        private set => SetProperty(ref _steamPath, value);
    }

    /// <summary>The validated executable path currently used to launch Deadlock.</summary>
    public string DeadlockPath
    {
        get => _deadlockPath;
        private set => SetProperty(ref _deadlockPath, value);
    }

    public string DeadlockPathDisplay
    {
        get => _deadlockPathDisplay;
        private set => SetProperty(ref _deadlockPathDisplay, value);
    }

    public string ExtraArguments
    {
        get => _extraArguments;
        set
        {
            if (SetProperty(ref _extraArguments, value ?? string.Empty))
            {
                _settings.ExtraLaunchArguments = _extraArguments;
                UpdateCommandPreview();
            }
        }
    }

    public string CommandPreview
    {
        get => _commandPreview;
        private set => SetProperty(ref _commandPreview, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (SetProperty(ref _isLaunching, value))
            {
                OnPropertyChanged(nameof(PlayButtonText));
                NotifyLaunchStateChanged();
            }
        }
    }

    public bool CanLaunch => DeadlockInstalled && !DeadlockRunning && !IsLaunching;

    /// <summary>Fast force-exit is offered whenever a Deadlock process is running.</summary>
    public bool CanStopDeadlock => DeadlockRunning;

    public bool IsReplaysPage
    {
        get => _isReplaysPage;
        private set => SetProperty(ref _isReplaysPage, value);
    }

    public string PlayButtonText => IsLaunching
        ? "LAUNCHING REPLAY..."
        : DeadlockRunning
            ? "REPLAY RUNNING"
            : SelectedReplay is null
                ? "SELECT A REPLAY"
                : "PLAY SELECTED REPLAY";

    public ReplayInfo? SelectedReplay
    {
        get => _selectedReplay;
        set
        {
            if (!SetProperty(ref _selectedReplay, value))
                return;

            _settings.SelectedReplayPath = value?.FullPath ?? string.Empty;
            _settings.Save();
            OnPropertyChanged(nameof(SelectedReplayName));
            OnPropertyChanged(nameof(SelectedReplayPathText));
            OnPropertyChanged(nameof(PlayButtonText));
            NotifyLaunchStateChanged();
        }
    }

    public void SelectReplay(ReplayInfo replay)
    {
        SelectedReplay = Replays.FirstOrDefault(r =>
            string.Equals(r.FullPath, replay.FullPath, StringComparison.OrdinalIgnoreCase))
            ?? replay;
    }

    public string SelectedReplayName =>
        SelectedReplay?.FileName ?? "No replay selected";

    public string SelectedReplayPathText =>
        SelectedReplay?.FullPath ?? "Select a demo from the list.";

    public string ReplayDirectoryPath
    {
        get => _replayDirectoryPath;
        private set => SetProperty(ref _replayDirectoryPath, value);
    }

    public string ReplayStatusText
    {
        get => _replayStatusText;
        private set => SetProperty(ref _replayStatusText, value);
    }

    public void Start()
    {
        Refresh();
        _timer.Start();
    }

    public void Refresh()
    {
        RefreshProcesses();
        OnPropertyChanged(nameof(LaunchViaSteam));
        OnPropertyChanged(nameof(SmvmForwardHotkey));
        OnPropertyChanged(nameof(SmvmBackHotkey));
        OnPropertyChanged(nameof(SmvmLeftHotkey));
        OnPropertyChanged(nameof(SmvmRightHotkey));
        OnPropertyChanged(nameof(SmvmUpHotkey));
        OnPropertyChanged(nameof(SmvmDownHotkey));
        OnPropertyChanged(nameof(SmvmFastHotkey));
        OnPropertyChanged(nameof(SmvmPrecisionHotkey));
        OnPropertyChanged(nameof(SmvmRollLeftHotkey));
        OnPropertyChanged(nameof(SmvmRollRightHotkey));
        OnPropertyChanged(nameof(SmvmRollResetHotkey));
        OnPropertyChanged(nameof(SmvmMenuHotkey));
        OnPropertyChanged(nameof(SmvmAddHotkey));
        OnPropertyChanged(nameof(SmvmDeleteHotkey));
        OnPropertyChanged(nameof(SmvmCleanViewHotkey));
        OnPropertyChanged(nameof(SmvmPlayStartHotkey));
        OnPropertyChanged(nameof(SmvmPlayCurrentHotkey));
        OnPropertyChanged(nameof(SmvmStopHotkey));
        OnPropertyChanged(nameof(SmvmUndoHotkey));
        OnPropertyChanged(nameof(SmvmRedoHotkey));
        OnPropertyChanged(nameof(SmvmShowPathHotkey));
        OnPropertyChanged(nameof(SmvmShowCamerasHotkey));
        OnPropertyChanged(nameof(SmvmRestoreUiHotkey));
        OnPropertyChanged(nameof(SmvmCycleUiHotkey));
        OnPropertyChanged(nameof(SmvmToggleFreeCameraHotkey));
        OnPropertyChanged(nameof(SmvmReplayPauseHotkey));
        OnPropertyChanged(nameof(SmvmShowLabelsHotkey));
        OnPropertyChanged(nameof(SmvmStepBackHotkey));
        OnPropertyChanged(nameof(SmvmStepForwardHotkey));
        OnPropertyChanged(nameof(SmvmEffectsHotkey));
        OnPropertyChanged(nameof(SmvmCinematicStartHotkey));
        OnPropertyChanged(nameof(SmvmPlaybackSlowerHotkey));
        OnPropertyChanged(nameof(SmvmPlaybackFasterHotkey));
        OnPropertyChanged(nameof(SmvmCancelHotkey));
        OnPropertyChanged(nameof(SmvmCameraSlowerHotkey));
        OnPropertyChanged(nameof(SmvmCameraFasterHotkey));


        var configuredGame = _steam.ValidateGamePath(_settings.DeadlockPath);
        var steam = _steam.DetectSteam();
        SteamInstalled = steam.IsInstalled;
        SteamPath = steam.InstallPath ?? string.Empty;

        if (configuredGame is not null)
        {
            SetGameLocation(configuredGame, persist: false);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(_settings.DeadlockPath))
            {
                _settings.DeadlockPath = string.Empty;
                _settings.Save();
            }

            var automaticGame = !string.IsNullOrWhiteSpace(steam.InstallPath)
                ? _steam.LocateGame(steam.InstallPath)
                : null;

            if (automaticGame is not null)
                SetGameLocation(automaticGame, persist: false);
            else
                ClearGameLocation();
        }

        UpdateStatusText();
        RefreshReplays();
        _log.Info(
            $"Detection: Steam={FormatDetection(SteamInstalled, SteamPath)}, " +
            $"Deadlock={FormatDetection(DeadlockInstalled, DeadlockPath)}, " +
            $"SteamRunning={SteamRunning}, DeadlockRunning={DeadlockRunning}");

        UpdateCommandPreview();
        NotifyLaunchStateChanged();
    }

    public bool SetManualDeadlockPath(string path)
    {
        var game = _steam.ValidateGamePath(path);
        if (game is null)
        {
            StatusMessage = "Select a valid Deadlock executable or installation folder.";
            _log.Warn($"Invalid Deadlock path selected: {path}");
            return false;
        }

        SetGameLocation(game, persist: true);
        UpdateStatusText();
        RefreshReplays();
        UpdateCommandPreview();
        NotifyLaunchStateChanged();
        StatusMessage = "Deadlock installation selected.";
        _log.Info($"Manual Deadlock path selected: {DeadlockPath}");
        return true;
    }

    private void RefreshProcesses()
    {
        _process.Refresh();
        SteamRunning = _process.IsSteamRunning;
        DeadlockRunning = _process.IsDeadlockRunning;
        UpdateStatusText();
    }

    /// <summary>
    /// Force-terminates Deadlock immediately. The PID from launch is not
    /// retained (Steam owns the process), so the running game is located by
    /// process name and killed without a graceful shutdown wait.
    /// </summary>
    private void StopDeadlock()
    {
        var terminated = DeadlockProcessControl.ForceExit(out var error);
        if (terminated > 0)
        {
            _log.Info($"Exit Deadlock: force-terminated {terminated} Deadlock process(es).");
            StatusMessage = "Deadlock closed.";
            DeadlockRunning = false;
        }
        else
        {
            _log.Warn($"Exit Deadlock: no running Deadlock process was found. {error}");
            StatusMessage = "No running Deadlock process was found.";
        }
    }

    private void Launch()
    {
        if (IsLaunching || DeadlockRunning)
        {
            _log.Info("Ignored replay launch because Deadlock is already opening or running.");
            return;
        }

        if (!DeadlockInstalled)
        {
            StatusMessage = "Deadlock installation not found.";
            _log.Warn("Launch attempted but Deadlock is not installed.");
            return;
        }

        if (SelectedReplay is null)
        {
            StatusMessage = "Select a replay to play.";
            ShowReplaysPage();
            return;
        }

        var replay = SelectedReplay;
        var additionalArguments = MovieModeLaunchPolicy.NormalizeAdditionalArguments(ExtraArguments);
        if (!string.Equals(additionalArguments, ExtraArguments, StringComparison.Ordinal))
            ExtraArguments = additionalArguments;
        _settings.ExtraLaunchArguments = additionalArguments;
        _settings.Save();

        var gameArguments = MovieModeLaunchPolicy.BuildReplayArguments(
            additionalArguments, replay.GamePath);
        var steamExecutable = LaunchViaSteam ? ResolveSteamExecutable() : null;
        if (LaunchViaSteam && steamExecutable is null)
            _log.Warn("Launch through Steam was requested but steam.exe was not found; falling back to direct launch.");
        var launchThroughSteam = steamExecutable is not null;
        var request = new LaunchRequest
        {
            ExecutablePath = steamExecutable ?? _gameExecutablePath,
            WorkingDirectory = steamExecutable is not null
                ? Path.GetDirectoryName(steamExecutable) ?? string.Empty
                : Path.GetDirectoryName(_gameExecutablePath) ?? string.Empty,
            BaseArguments = launchThroughSteam
                ? new[] { "-applaunch", DeadlockConstants.AppIdString }
                : Array.Empty<string>(),
            ExtraArguments = gameArguments,
        };

        var fullCommandLine = CommandLine.Join(
            request.BaseArguments.Concat(request.ExtraArguments));

        _log.Info($"Launch requested: \"{request.ExecutablePath}\" {fullCommandLine}");
        _log.Info($"Replay playback requested: {replay.FileName} as '{replay.GamePath}'");

        IsLaunching = true;
        var launchOperation = Interlocked.Increment(ref _launchOperationGeneration);
        var launchAttempt = Interlocked.Increment(ref _launchAttemptGeneration);
        StatusMessage = "Launching replay...";

        var result = _launcher.Launch(request);

        if (result.Success)
        {
            _log.Info($"Launch OK — PID {result.ProcessId}, command: {result.FullCommandLine}");
            if (launchThroughSteam)
            {
                BeginEarlyNativeLoadForSteamLaunch(launchOperation);
            }
            else if (result.ProcessId is int processId)
            {
                // Load the narrow replay-only component immediately, before
                // Deadlock creates its real DXGI swapchain. The session monitor
                // remains the retry/reconnect path if this earliest attempt
                // loses a startup race or the process rejects the load.
                BeginEarlyNativeLoadForDirectLaunch(launchOperation, processId);
            }
            StatusMessage = "Launching replay...";
            BeginLaunchDetectionTimeout(launchAttempt, replay.FileName);
            BeginVerifyPlayback(launchOperation, replay.GamePath, launchThroughSteam);
        }
        else
        {
            _log.Error(result.Message);
            StatusMessage = result.Message;
            CompleteLaunchOpening();
        }

        LaunchCompleted?.Invoke(this, result.Success);
    }

    private void BeginLaunchDetectionTimeout(int launchAttempt, string replayFileName)
    {
        var uiScheduler = SynchronizationContext.Current is { }
            ? TaskScheduler.FromCurrentSynchronizationContext()
            : TaskScheduler.Default;

        _ = Task.Run(async () =>
            {
                await Task.Delay(ReplayLaunchDetectionTimeout).ConfigureAwait(false);
            })
            .ContinueWith(
                _ =>
                {
                    if (!IsLaunching || launchAttempt != Volatile.Read(ref _launchAttemptGeneration))
                        return;

                    CompleteLaunchOpening();
                    StatusMessage = $"Deadlock did not open for {replayFileName}. You can try again.";
                    _log.Warn(
                        $"Replay launch lock timed out after {ReplayLaunchDetectionTimeout.TotalSeconds:0} seconds; " +
                        "no Deadlock process was detected.");
                    LaunchCompleted?.Invoke(this, false);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                uiScheduler);
    }

    private void CompleteLaunchOpening()
    {
        Interlocked.Increment(ref _launchAttemptGeneration);
        IsLaunching = false;
    }

    public void NotifyLauncherHidden() =>
        _log.Info("Launcher window hidden while Deadlock runs; managed SMVM host remains active.");

    public void RestoreAfterDeadlockExit()
    {
        Interlocked.Increment(ref _launchOperationGeneration);
        Refresh();
        StatusMessage = "Deadlock closed. Launcher restored.";
        _log.Info("Deadlock exited; launcher window restored and replay state refreshed.");
    }

    private string? ResolveSteamExecutable()
    {
        if (string.IsNullOrWhiteSpace(SteamPath))
            return null;
        var executable = Path.Combine(SteamPath, "steam.exe");
        return File.Exists(executable) ? executable : null;
    }

    private void BeginEarlyNativeLoadForDirectLaunch(int launchOperation, int processId)
    {
        var nativePath = Path.Combine(AppContext.BaseDirectory, "DeadlockMVM.Native.dll");
        var expectedGamePath = Path.GetFullPath(_gameExecutablePath);
        _ = Task.Run(() =>
        {
            if (launchOperation != Volatile.Read(ref _launchOperationGeneration))
                return;
            var nativeLoad = NativeReplayModuleLoader.LoadForReplay(
                processId, nativePath, expectedGamePath);
            if (nativeLoad.Success)
                _log.Info($"Early native load: {nativeLoad.Message}");
            else
                _log.Warn($"Early native load unavailable; monitor will retry: {nativeLoad.Message}");
        });
    }

    private void BeginEarlyNativeLoadForSteamLaunch(int launchOperation)
    {
        var nativePath = Path.Combine(AppContext.BaseDirectory, "DeadlockMVM.Native.dll");
        var expectedGamePath = Path.GetFullPath(_gameExecutablePath);
        _ = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            string? lastFailure = null;
            while (DateTime.UtcNow < deadline)
            {
                if (launchOperation != Volatile.Read(ref _launchOperationGeneration))
                    return;
                foreach (var processName in new[]
                         {
                             DeadlockConstants.GameProcessName,
                             DeadlockConstants.GameProcessNameAlt,
                         })
                {
                    var processes = Process.GetProcessesByName(processName);
                    try
                    {
                        foreach (var process in processes)
                        {
                            try
                            {
                                if (process.HasExited || process.MainModule?.FileName is not { } processPath ||
                                    !string.Equals(Path.GetFullPath(processPath), expectedGamePath,
                                        StringComparison.OrdinalIgnoreCase))
                                    continue;

                                if (launchOperation != Volatile.Read(ref _launchOperationGeneration))
                                    return;
                                var nativeLoad = NativeReplayModuleLoader.LoadForReplay(
                                    process.Id, nativePath, expectedGamePath);
                                if (nativeLoad.Success)
                                {
                                    _log.Info($"Early Steam native load: {nativeLoad.Message}");
                                    return;
                                }
                                lastFailure = nativeLoad.Message;
                            }
                            catch (InvalidOperationException)
                            {
                                // The short-lived process disappeared between discovery and inspection.
                            }
                            catch (System.ComponentModel.Win32Exception ex)
                            {
                                lastFailure = ex.Message;
                            }
                        }
                    }
                    finally
                    {
                        foreach (var process in processes)
                            process.Dispose();
                    }
                }

                await Task.Delay(20).ConfigureAwait(false);
            }

            _log.Warn($"Early Steam native load timed out; monitor will retry: {lastFailure ?? "Deadlock process was not observed."}");
        });
    }

    /// <summary>
    /// Watches Deadlock's console log for engine confirmation that the demo
    /// actually started. The game truncates the log on every launch, so the
    /// whole file is scanned; readiness comes from file writes, not sleeps.
    /// </summary>
    private void BeginVerifyPlayback(int launchOperation, string gamePath, bool launchedThroughSteam)
    {
        var consoleLogPath = DeadlockConsoleLog.GetConsoleLogPath(_gameExecutablePath);
        var startedAfterLocal = DateTime.Now.AddMinutes(-1);
        var uiScheduler = SynchronizationContext.Current is { }
            ? TaskScheduler.FromCurrentSynchronizationContext()
            : TaskScheduler.Default;

        _log.Info($"Verifying playback via Deadlock console log: {consoleLogPath ?? "<unavailable>"}");

        _ = Task.Run(async () =>
        {
            var confirmed = false;
            var deadline = DateTime.UtcNow.AddSeconds(150);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000).ConfigureAwait(false);

                if (launchOperation != Volatile.Read(ref _launchOperationGeneration))
                    return;

                if (DeadlockConsoleLog.ContainsPlaybackConfirmation(
                        consoleLogPath, gamePath, startedAfterLocal))
                {
                    confirmed = true;
                    break;
                }
            }

            await Task.Factory.StartNew(
                () =>
                {
                    if (launchOperation != Volatile.Read(ref _launchOperationGeneration))
                        return;
                    StatusMessage = confirmed
                        ? "Replay playback confirmed."
                        : launchedThroughSteam
                            ? "Launched via Steam but playback was not confirmed. Turn off 'Launch through Steam' and try direct launch."
                            : "Could not confirm playback in Deadlock's console log.";
                    _log.Info(confirmed
                        ? $"Playback confirmed by engine: '{gamePath}.dem'"
                        : $"Playback NOT confirmed within timeout: '{gamePath}.dem' (viaSteam={launchedThroughSteam}). " +
                          "If via Steam, retry with direct launch so +playdemo stays on the game command line.");
                },
                CancellationToken.None,
                TaskCreationOptions.None,
                uiScheduler);
        });
    }

    private void OpenLogs()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _log.LogFilePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to open log file: {ex.Message}");
            StatusMessage = "Could not open the log file.";
        }
    }

    private void ResetDeadlockPath()
    {
        _settings.DeadlockPath = string.Empty;
        _settings.Save();
        StatusMessage = "Searching Steam libraries...";
        Refresh();
    }

    public void RefreshReplays()
    {
        var savedReplayPath = _settings.SelectedReplayPath;
        SelectedReplay = null;
        Replays.Clear();

        if (string.IsNullOrWhiteSpace(_gameExecutablePath))
        {
            ReplayDirectoryPath = string.Empty;
            ReplayStatusText = "Deadlock installation not found.";
            return;
        }

        ReplayDirectoryPath = _replayService.GetReplayDirectory(_gameExecutablePath) ?? string.Empty;
        foreach (var replay in _replayService.ScanReplays(_gameExecutablePath))
            Replays.Add(replay);

        var restoredReplay = Replays.FirstOrDefault(replay =>
            string.Equals(replay.FullPath, savedReplayPath, StringComparison.OrdinalIgnoreCase));
        SelectedReplay = restoredReplay;

        ReplayStatusText = ReplayDirectoryPath.Length == 0 || !Directory.Exists(ReplayDirectoryPath)
            ? "Replay folder unavailable."
            : Replays.Count == 0
                ? "No demos found."
                : $"{Replays.Count} demo{(Replays.Count == 1 ? string.Empty : "s")} found.";

        _log.Info($"Replay scan: directory={ReplayDirectoryPath}, count={Replays.Count}");
    }

    public void ImportReplay(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(_gameExecutablePath))
        {
            ReplayStatusText = "Deadlock installation not found.";
            return;
        }

        var result = _replayService.ImportReplay(sourcePath, _gameExecutablePath);
        if (!result.Success || result.Replay is null)
        {
            ReplayStatusText = result.Message;
            _log.Warn(result.Message);
            return;
        }

        RefreshReplays();
        SelectedReplay = Replays.FirstOrDefault(replay =>
            string.Equals(replay.FullPath, result.Replay.FullPath, StringComparison.OrdinalIgnoreCase));
        ReplayStatusText = result.Message;
        _log.Info(result.Message);
    }

    private void OpenReplayFolder()
    {
        if (_replayService.OpenReplayDirectory(_gameExecutablePath))
        {
            ReplayStatusText = "Replay folder opened.";
            return;
        }

        ReplayStatusText = "Replay folder could not be opened.";
    }

    private void ShowReplaysPage()
    {
        IsReplaysPage = true;
        RefreshReplays();
    }

    private void UseSelectedReplay()
    {
        if (SelectedReplay is not null)
            StatusMessage = $"Selected {SelectedReplay.FileName}.";

        IsReplaysPage = false;
    }

    private void SetGameLocation(GameLocation location, bool persist)
    {
        if (!location.IsInstalled || string.IsNullOrWhiteSpace(location.ExecutablePath))
        {
            ClearGameLocation();
            return;
        }

        _gameExecutablePath = location.ExecutablePath;
        DeadlockPath = location.ExecutablePath;
        DeadlockPathDisplay = location.ExecutablePath;
        DeadlockInstalled = true;

        if (persist)
        {
            _settings.DeadlockPath = location.ExecutablePath;
            _settings.Save();
        }
    }

    private void ClearGameLocation()
    {
        _gameExecutablePath = string.Empty;
        DeadlockPath = string.Empty;
        DeadlockPathDisplay = "Use Locate Deadlock to select the game.";
        DeadlockInstalled = false;
    }

    private void UpdateStatusText()
    {
        SteamStatusText = SteamRunning
            ? "Running"
            : SteamInstalled
                ? "Not running"
                : "Not detected";

        DeadlockInstallText = DeadlockInstalled
            ? "Installation detected"
            : "Deadlock installation not found";

        DeadlockStateText = DeadlockRunning
            ? "Running"
            : DeadlockInstalled
                ? "Ready"
                : "Not found";

        PathActionText = DeadlockInstalled ? "Change" : "Locate Deadlock";
    }

    private void UpdateCommandPreview()
    {
        if (string.IsNullOrWhiteSpace(_gameExecutablePath))
        {
            CommandPreview = "Deadlock installation not found.";
            return;
        }

        var arguments = MovieModeLaunchPolicy.BuildPreviewArguments(ExtraArguments);
        var steamExecutable = LaunchViaSteam ? ResolveSteamExecutable() : null;
        if (steamExecutable is not null)
        {
            var steamArguments = new[] { "-applaunch", DeadlockConstants.AppIdString }
                .Concat(arguments);
            CommandPreview = $"\"{steamExecutable}\" {CommandLine.Join(steamArguments)}";
            return;
        }

        CommandPreview = $"\"{_gameExecutablePath}\" {CommandLine.Join(arguments)}";
    }

    private static string FormatDetection(bool found, string path)
        => found ? $"found ({path})" : "not found";

    private void NotifyLaunchStateChanged()
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanStopDeadlock));
        (LaunchCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopDeadlockCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
