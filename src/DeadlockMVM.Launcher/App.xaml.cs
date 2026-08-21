using System.Windows;
using DeadlockMVM.Core;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;
using DeadlockMVM.Launcher.Director;
using DeadlockMVM.Launcher.ViewModels;

namespace DeadlockMVM.Launcher;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        log.Info("=== Deadlock MVM Launcher v0.1.0 started ===");

        try
        {
            // Core MVM components
            IGameCommandTransport transport = new VConsoleTransport();
            var controller = new ReplayController(transport);
            ICameraService camera = new ReplayCameraService(transport);

            var viewModel = new MainViewModel(steam, process, launcher, log, settings, replayService);

            // Director window (hidden initially; shown on playback confirm or hotkey)
            var director = new DirectorWindow(camera, controller, settings, log);
            viewModel.SetDirector(director);

            var window = new MainWindow { DataContext = viewModel };
            window.Closed += (_, _) =>
            {
                director.Close();
                controller.Dispose();
                settings.Save();
                log.Info("=== Launcher closed ===");
            };

            MainWindow = window;
            window.Show();

            viewModel.Start();
        }
        catch (Exception ex)
        {
            log.Error($"Fatal startup error: {ex}");
            System.Windows.MessageBox.Show(ex.Message, "Deadlock MVM Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
