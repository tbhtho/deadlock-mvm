using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;
using DeadlockMVM.Launcher.ViewModels;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace DeadlockMVM.Launcher;

public partial class MainWindow : Window
{
    private readonly LauncherWindowLifecycle _launcherLifecycle = new();
    private DevConsoleWindow? _devConsole;

    /// <summary>Shared VConsole controller, assigned by App for the optional dev console.</summary>
    public ReplayController? ConsoleSource { get; set; }

    private void OpenDevConsole_Click(object sender, RoutedEventArgs e)
    {
        if (ConsoleSource is not { } controller)
        {
            System.Windows.MessageBox.Show(
                this,
                "The Deadlock console connection is not ready yet.",
                "Deadlock MVM",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_devConsole is { IsVisible: true })
        {
            _devConsole.Activate();
            return;
        }

        _devConsole = new DevConsoleWindow(controller) { Owner = this };
        _devConsole.Closed += (_, _) => _devConsole = null;
        _devConsole.Show();
    }

    private void OpenKeyboardBindings_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) new KeyboardBindingsWindow(vm) { Owner = this }.ShowDialog();
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += MainWindow_DataContextChanged;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel previous)
            Unsubscribe(previous);
        if (e.NewValue is MainViewModel current)
            Subscribe(current);
    }

    private void Subscribe(MainViewModel viewModel)
    {
        viewModel.LaunchCompleted += ViewModel_LaunchCompleted;
        viewModel.DeadlockRunningChanged += ViewModel_DeadlockRunningChanged;
        viewModel.LauncherVisibilityPreferenceChanged += ViewModel_LauncherVisibilityPreferenceChanged;
    }

    private void Unsubscribe(MainViewModel viewModel)
    {
        viewModel.LaunchCompleted -= ViewModel_LaunchCompleted;
        viewModel.DeadlockRunningChanged -= ViewModel_DeadlockRunningChanged;
        viewModel.LauncherVisibilityPreferenceChanged -= ViewModel_LauncherVisibilityPreferenceChanged;
    }

    private void ViewModel_LaunchCompleted(object? sender, bool success)
    {
        if (success)
            _launcherLifecycle.ArmSuccessfulLaunch(DateTime.UtcNow);
        else
            _launcherLifecycle.CancelLaunch();
        ApplyLauncherLifecycle();
    }

    private void ViewModel_DeadlockRunningChanged(object? sender, bool running) =>
        ApplyLauncherLifecycle();

    private void ViewModel_LauncherVisibilityPreferenceChanged(object? sender, EventArgs e) =>
        ApplyLauncherLifecycle();

    private void ApplyLauncherLifecycle()
    {
        var viewModel = ViewModel;
        if (viewModel is null)
            return;

        switch (_launcherLifecycle.Observe(
                    viewModel.DeadlockRunning,
                    viewModel.HideLauncherWhileDeadlockRunning,
                    DateTime.UtcNow))
        {
            case LauncherWindowAction.Hide:
                ShowInTaskbar = false;
                Hide();
                viewModel.NotifyLauncherHidden();
                break;
            case LauncherWindowAction.Restore:
                ShowInTaskbar = true;
                Show();
                WindowState = WindowState.Normal;
                viewModel.RestoreAfterDeadlockExit();
                Activate();
                break;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ViewModel?.DeadlockRunning == true)
        {
            var choice = System.Windows.MessageBox.Show(
                "Deadlock is still running. Closing Deadlock MVM will disconnect SMVM and stop the managed host. Close anyway?",
                "Close Deadlock MVM?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (choice != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (ViewModel is { } viewModel)
            Unsubscribe(viewModel);
        base.OnClosed(e);
    }

    private void LocateDeadlockButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "Select the Deadlock executable",
            Filter = "Deadlock executable (deadlock.exe;project8.exe)|deadlock.exe;project8.exe|Executable files (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
            ViewModel?.SetManualDeadlockPath(dialog.FileName);
    }

    private void LocateDeadlockFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the Deadlock installation folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
            ViewModel?.SetManualDeadlockPath(dialog.FolderName);
    }

    private void ImportReplayButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "Import Deadlock demo",
            Filter = "Demo files (*.dem)|*.dem|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
            ViewModel?.ImportReplay(dialog.FileName);
    }

    private void ReplayList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is WpfListBox list && list.SelectedItem is ReplayInfo replay)
            ViewModel?.SelectReplay(replay);
    }
}
