using System.Windows;
using System.IO;
using DeadlockMVM.Core;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Native;
using DeadlockMVM.Core.Native.InProcess;
using DeadlockMVM.Core.Services;
using DeadlockMVM.Launcher.Smvm;
using DeadlockMVM.Launcher.ViewModels;

namespace DeadlockMVM.Launcher;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var launchSelectedReplay = e.Args.Any(argument =>
            string.Equals(argument, "--launch-selected", StringComparison.OrdinalIgnoreCase));

        // Deadlock's VConsole serves a single client; two MVM instances would
        // fight over the console connection. Only one instance may run.
        _singleInstanceMutex = new Mutex(true, "DeadlockMVM.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            System.Windows.MessageBox.Show(
                "Deadlock MVM is already running.",
                "Deadlock MVM",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var settings = new AppSettings();
        settings.Load();

        var log = new FileLogService();
        var steam = new SteamService();
        var process = new ProcessMonitor(new SystemProcessQuery());
        var launcher = new GameLauncher();
        var replayService = new ReplayService();

        log.Info($"=== {ProductInfo.Name} Launcher {ProductInfo.DisplayVersion} started ===");

        try
        {
            // Core MVM components
            IGameCommandTransport transport = new VConsoleTransport();
            var controller = new ReplayController(transport);
            // VConsole camera path (transform/position/POV/roam) composed with the
            // native replay-camera backend (active FOV, roaming rotation).
            var nativeCamera = new NativeCameraBackend(log);
            ICameraService camera = new CompositeCameraService(
                new ReplayCameraService(transport), nativeCamera, controller, log);
            var viewModel = new MainViewModel(steam, process, launcher, log, settings, replayService);
            var nativeSession = new NativeReplayCameraSession(
                controller,
                camera,
                log,
                Path.Combine(AppContext.BaseDirectory, "DeadlockMVM.Native.dll"),
                () => viewModel.DeadlockPath);

            // Internal SMVM editor: campath model, startup-only pointer
            // fallback, and the VConsole auto-connect loop. Once the healthy
            // native overlay owns the game window, ordinary Win32 messages
            // must pass through so held ImGui gestures retain their exact
            // down/move/up lifecycle.
            var pointerForwarder = new SmvmPointerForwarder(
                Dispatcher,
                () =>
                {
                    var status = nativeSession.Status;
                    return status?.OverlayFlags.HasFlag(SmvmOverlayFlags.MenuOpen) == true &&
                           (!status.OverlayFlags.HasFlag(SmvmOverlayFlags.Ready) ||
                            status.RendererError == SmvmRendererError.WindowHookFailed);
                },
                PointerTraceEnabled() ? message => log.Info(message) : null);
            if (!pointerForwarder.IsAvailable)
                log.Warn($"SMVM: menu pointer observer unavailable (error {pointerForwarder.LastError}).");
            var campath = new CampathViewModel(camera, controller, nativeSession, settings, log);
            var connection = new VConsoleConnectionService(controller, settings.VConsolePort, log, Dispatcher);
            var smvm = new SmvmHostCoordinator(
                camera,
                controller,
                nativeSession,
                campath,
                settings,
                log,
                Dispatcher,
                () => viewModel.DeadlockPath);
            connection.Start();

            var window = new MainWindow { DataContext = viewModel };
            // Optional first-party dev console reuses this single VConsole
            // connection; the game's own console is left alone.
            window.ConsoleSource = controller;
            window.Closed += async (_, _) =>
            {
                await smvm.DisposeAsync();
                campath.Shutdown();
                pointerForwarder.Dispose();
                connection.Dispose();
                await nativeSession.DisposeAsync();
                controller.Dispose();
                settings.Save();
                log.Info("=== Launcher closed ===");
                Shutdown();
            };

            MainWindow = window;
            window.Show();

            viewModel.Start();
            if (launchSelectedReplay)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (viewModel.LaunchCommand.CanExecute(null))
                    {
                        log.Info("Development launch switch requested the selected replay.");
                        viewModel.LaunchCommand.Execute(null);
                    }
                    else
                    {
                        log.Warn("Development launch switch could not run because the selected replay is unavailable.");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            log.Error($"Fatal startup error: {ex}");
            System.Windows.MessageBox.Show(ex.Message, "Deadlock MVM Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    // Per-pointer-event traces are dev diagnostics; they stay out of the normal
    // Release log unless explicitly requested via the environment.
    private static bool PointerTraceEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("DEADLOCKMVM_POINTER_TRACE"),
            "1",
            StringComparison.Ordinal);
}
