using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using DeadlockMVM.Core;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;
using DeadlockMVM.Launcher.Director;

namespace DeadlockMVM.Launcher.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly ISteamService _steam;
    private readonly IProcessMonitor _process;
    private readonly IGameLauncher _launcher;
    private readonly IReplayService _replayService;
    private readonly ILogService _log;
    private readonly IAppSettings _settings;
    private readonly DispatcherTimer _timer;
    private DirectorWindow? _director;

    private string _gameExecutablePath = string.Empty;
    private string _extraArguments = string.Empty;
    private string _directorHotkey = "Ctrl+Alt+M";
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
        _extraArguments = string.IsNullOrWhiteSpace(settings.ExtraLaunchArguments)
            ? DefaultLaunchArguments
            : settings.ExtraLaunchArguments;
        _directorHotkey = settings.DirectorHotkey;

        LaunchCommand = new RelayCommand(Launch, () => CanLaunch);
        RefreshCommand = new RelayCommand(Refresh);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        ResetDeadlockPathCommand = new RelayCommand(ResetDeadlockPath);
        RefreshReplaysCommand = new RelayCommand(RefreshReplays);
        OpenReplayFolderCommand = new RelayCommand(OpenReplayFolder);
        ShowHomeCommand = new RelayCommand(() => IsReplaysPage = false);
        ShowReplaysCommand = new RelayCommand(ShowReplaysPage);
        UseSelectedReplayCommand = new RelayCommand(UseSelectedReplay);
        OpenDirectorCommand = new RelayCommand(OpenDirector, () => _director is not null);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => RefreshProcesses();
    }

    private static string DefaultLaunchArguments => string.Join(' ', DeadlockConstants.MovieModeArguments);

    public ICommand LaunchCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand OpenLogsCommand { get; }

    public ICommand ResetDeadlockPathCommand { get; }

    public ICommand RefreshReplaysCommand { get; }

    public ICommand OpenReplayFolderCommand { get; }

    public ICommand ShowHomeCommand { get; }

    public ICommand ShowReplaysCommand { get; }

    public ICommand UseSelectedReplayCommand { get; }

    public ICommand OpenDirectorCommand { get; }

    public void SetDirector(DirectorWindow director)
    {
        _director = director;
        (OpenDirectorCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

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
        private set => SetProperty(ref _deadlockRunning, value);
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

    public string DirectorHotkey
    {
        get => _directorHotkey;
        set
        {
            if (!SetProperty(ref _directorHotkey, value ?? string.Empty))
                return;

            _settings.DirectorHotkey = _directorHotkey;
            _settings.Save();
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
                NotifyLaunchStateChanged();
        }
    }

    public bool CanLaunch => DeadlockInstalled && !IsLaunching;

    public bool IsReplaysPage
    {
        get => _isReplaysPage;
        private set => SetProperty(ref _isReplaysPage, value);
    }

    public string PlayButtonText =>
        SelectedReplay is null ? "SELECT A REPLAY" : "PLAY SELECTED REPLAY";

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

    private void Launch()
    {
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
        _settings.ExtraLaunchArguments = ExtraArguments;
        _settings.Save();

        var request = new LaunchRequest
        {
            ExecutablePath = _gameExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(_gameExecutablePath) ?? string.Empty,
            BaseArguments = Array.Empty<string>(),
            ExtraArguments = CommandLine
                .Tokenize(ExtraArguments)
                .Concat(new[] { "+playdemo", replay.GamePath })
                .ToArray(),
        };

        var fullCommandLine = CommandLine.Join(
            request.BaseArguments.Concat(request.ExtraArguments));

        _log.Info($"Launch requested: \"{request.ExecutablePath}\" {fullCommandLine}");
        _log.Info($"Replay playback requested: {replay.FileName} as '{replay.GamePath}'");

        IsLaunching = true;
        StatusMessage = $"Launching {replay.FileName}...";

        var result = _launcher.Launch(request);

        if (result.Success)
        {
            _log.Info($"Launch OK — PID {result.ProcessId}, command: {result.FullCommandLine}");
            StatusMessage = $"Playing {replay.FileName}...";
            BeginVerifyPlayback(replay.GamePath);
        }
        else
        {
            _log.Error(result.Message);
            StatusMessage = result.Message;
        }

        IsLaunching = false;
    }

    /// <summary>
    /// Watches Deadlock's console log for engine confirmation that the demo
    /// actually started. The game truncates the log on every launch, so the
    /// whole file is scanned; readiness comes from file writes, not sleeps.
    /// </summary>
    private void BeginVerifyPlayback(string gamePath)
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
                    StatusMessage = confirmed
                        ? "Replay playback confirmed."
                        : "Could not confirm playback in Deadlock's console log.";
                    _log.Info(confirmed
                        ? $"Playback confirmed by engine: '{gamePath}.dem'"
                        : $"Playback NOT confirmed within timeout: '{gamePath}.dem'");

                    if (confirmed && _director is not null)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() => _director.ToggleVisibility());
                    }
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

    private void OpenDirector()
    {
        _director?.ToggleVisibility();
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

        var arguments = CommandLine.Tokenize(ExtraArguments);

        CommandPreview = $"\"{_gameExecutablePath}\" {CommandLine.Join(arguments)}";
    }

    private static string FormatDetection(bool found, string path)
        => found ? $"found ({path})" : "not found";

    private void NotifyLaunchStateChanged()
    {
        OnPropertyChanged(nameof(CanLaunch));
        (LaunchCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
