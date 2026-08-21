using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Launcher.ViewModels;
using Forms = System.Windows.Forms;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace DeadlockMVM.Launcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

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
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select the Deadlock installation folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            ViewModel?.SetManualDeadlockPath(dialog.SelectedPath);
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
