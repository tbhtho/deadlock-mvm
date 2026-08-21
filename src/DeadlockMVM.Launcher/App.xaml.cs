using System.Windows;
using DeadlockMVM.Core.Services;
using DeadlockMVM.Launcher.ViewModels;

namespace DeadlockMVM.Launcher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
            var viewModel = new MainViewModel(steam, process, launcher, log, settings, replayService);

            var window = new MainWindow { DataContext = viewModel };
            window.Closed += (_, _) =>
            {
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
}
