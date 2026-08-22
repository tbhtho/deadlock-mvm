using System.Windows;
using System.Windows.Input;
using DeadlockMVM.Core;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;

namespace DeadlockMVM.Launcher.Director;

public partial class DirectorWindow : Window
{
    private readonly DirectorViewModel _vm;
    private readonly GlobalHotkey _hotkey;
    private readonly WindowTracker _tracker;
    private readonly System.Windows.Threading.DispatcherTimer _connectTimer;
    private readonly System.Windows.Threading.DispatcherTimer? _hotkeyRetry;
    private readonly ILogService _log;
    private readonly int _vconPort;

    public DirectorWindow(ICameraService camera, ReplayController controller, IAppSettings settings, ILogService log)
    {
        InitializeComponent();

        _vm = new DirectorViewModel(camera, controller, settings, log);
        DataContext = _vm;
        _vconPort = settings.VConsolePort;
        _log = log;

        _hotkey = new GlobalHotkey(this, settings.DirectorHotkey, ToggleVisibility);
        if (_hotkey.IsRegistered)
        {
            log.Info($"Director: global hotkey {_hotkey.KeyName} registered.");
        }
        else
        {
            log.Warn($"Director: hotkey {_hotkey.KeyName} not registered yet (error {_hotkey.LastError}); retrying every 5 s.");
            _hotkeyRetry = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            _hotkeyRetry.Tick += (_, _) =>
            {
                if (!_hotkey.TryRegister())
                    return;

                _hotkeyRetry.Stop();
                log.Info($"Director: global hotkey {_hotkey.KeyName} registered after retry.");
            };
            _hotkeyRetry.Start();
        }
        _tracker = new WindowTracker();
        _tracker.WindowMoved += OnGameWindowMoved;

        // Auto-connect whenever Deadlock is running and we are not connected.
        // Runs from construction so state is live before the hotkey is pressed.
        _connectTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _connectTimer.Tick += (_, _) => TryConnect();
        _connectTimer.Start();
        TryConnect();

        Loaded += (_, _) => PositionRelativeToGame();
        Closed += (_, _) =>
        {
            _connectTimer.Stop();
            _hotkeyRetry?.Stop();
            _hotkey.Dispose();
            _tracker.Dispose();
        };

        // Don't steal focus on show
        ShowActivated = false;
    }

    private void TryConnect()
    {
        var controller = ((DirectorViewModel)DataContext).Controller;
        if (controller.IsConnected || !_tracker.IsGameRunning)
            return;

        try
        {
            controller.Connect("127.0.0.1", _vconPort);
        }
        catch
        {
            // Game not ready yet; the timer retries.
        }
    }

    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            _log.Info("Director: hidden.");
            Hide();
        }
        else
        {
            PositionRelativeToGame();
            Show();
            _log.Info($"Director: shown at {Left:F0},{Top:F0}.");
        }
    }

    private void OnGameWindowMoved(object? sender, Rect rect)
    {
        Dispatcher.Invoke(() => PositionRelativeToGame(rect));
    }

    private void PositionRelativeToGame(Rect? gameRect = null)
    {
        var rect = gameRect ?? _tracker.CurrentRect;
        if (rect is not { } r)
            return;

        // Position at right edge of game window, vertically centered
        var screen = SystemParameters.WorkArea;
        double left = r.Right - Width - 8;
        double top = r.Top + (r.Height - Height) / 2;

        // Clamp to screen
        left = Math.Max(screen.Left, Math.Min(left, screen.Right - Width));
        top = Math.Max(screen.Top, Math.Min(top, screen.Bottom - Height));

        Left = left;
        Top = top;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    // ---- Camera page FOV editor (input plumbing only; logic lives in CameraViewModel)

    // _vm is null while InitializeComponent runs; control events can fire that early.
    private CameraViewModel? Camera => _vm?.Camera;

    private void FovInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            Camera?.CommitFovInput();
            e.Handled = true;
        }
    }

    private void FovInput_GotFocus(object sender, RoutedEventArgs e)
        => Camera?.SetFovEditing(true);

    private void FovInput_LostFocus(object sender, RoutedEventArgs e)
        => Camera?.CommitFovInput();

    private void FovSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => Camera?.OnFovSliderChanged(e.NewValue);

    private void FovSlider_DragStarted(object sender, MouseButtonEventArgs e)
        => Camera?.SetFovEditing(true);

    private void FovSlider_DragCompleted(object sender, MouseButtonEventArgs e)
    {
        Camera?.SetFovEditing(false);
        Camera?.CommitFovSlider();
    }
}
