using System.Diagnostics;
using System.Windows.Threading;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Services;

namespace DeadlockMVM.Launcher.Smvm;

/// <summary>
/// Periodically connects the replay controller to Deadlock's VConsole whenever
/// the game process is running and the controller is not yet connected.
/// Replaces the auto-connect loop that used to live in the Director window.
/// </summary>
public sealed class VConsoleConnectionService : IDisposable
{
    private static readonly string[] GameProcessNames = ["project8", "deadlock"];

    private readonly ReplayController _controller;
    private readonly int _port;
    private readonly ILogService _log;
    private readonly DispatcherTimer _timer;

    public VConsoleConnectionService(
        ReplayController controller,
        int port,
        ILogService log,
        Dispatcher? dispatcher = null)
    {
        _controller = controller;
        _port = port;
        _log = log;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher ?? Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += (_, _) => TryConnect();
    }

    public void Start()
    {
        _timer.Start();
        TryConnect();
    }

    public void Dispose() => _timer.Stop();

    private void TryConnect()
    {
        if (_controller.IsConnected || !IsGameRunning())
            return;

        try
        {
            _controller.Connect("127.0.0.1", _port);
        }
        catch
        {
            // Game not ready yet; the timer retries.
        }
    }

    private static bool IsGameRunning()
    {
        foreach (var name in GameProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                        return true;
                }
            }
        }
        return false;
    }
}
