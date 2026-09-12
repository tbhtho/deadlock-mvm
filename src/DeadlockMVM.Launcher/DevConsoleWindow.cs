using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeadlockMVM.Core.Services;

namespace DeadlockMVM.Launcher;

/// <summary>
/// Optional first-party developer console themed for Deadlock MVM. It attaches
/// a read/command view to the already-owned VConsole connection instead of
/// opening a second socket, so replay telemetry keeps working and the game's
/// own developer console is unaffected.
/// </summary>
public sealed class DevConsoleWindow : Window
{
    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(0x12, 0x13, 0x13));
    private static readonly Brush SurfaceBrush = new SolidColorBrush(Color.FromRgb(0x19, 0x1A, 0x1C));
    private static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2E, 0x31));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xE8));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0x71, 0x6C));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0x87, 0x3C));

    private const int MaxCharacters = 200_000;
    private const int TrimToCharacters = 100_000;

    private readonly ReplayController controller;
    private readonly TextBox output = new();
    private readonly TextBox input = new();

    public DevConsoleWindow(ReplayController controller)
    {
        this.controller = controller;
        Title = "Developer Console - Deadlock MVM";
        Width = 800;
        Height = 540;
        MinWidth = 520;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = PanelBrush;
        Foreground = TextBrush;

        var root = new DockPanel { LastChildFill = true };

        var header = new Border
        {
            Background = SurfaceBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10, 14, 10),
        };
        var headerRow = new DockPanel();
        var info = new TextBlock
        {
            Text = "Same VConsole connection as MVM. The game's own console still works.",
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(info, Dock.Left);
        var clear = new Button
        {
            Content = "Clear",
            Foreground = AccentBrush,
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        clear.Click += (_, _) => output.Clear();
        DockPanel.SetDock(clear, Dock.Right);
        headerRow.Children.Add(clear);
        headerRow.Children.Add(info);
        header.Child = headerRow;
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var inputRow = new Border
        {
            Background = SurfaceBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10),
        };
        var inputRowContent = new DockPanel();
        var send = new Button
        {
            Content = "Send",
            Foreground = AccentBrush,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        send.Click += (_, _) => Submit();
        DockPanel.SetDock(send, Dock.Right);
        input.FontFamily = new FontFamily("Consolas");
        input.FontSize = 13;
        input.Padding = new Thickness(6);
        input.Background = PanelBrush;
        input.Foreground = TextBrush;
        input.CaretBrush = TextBrush;
        input.BorderBrush = LineBrush;
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            Submit();
            e.Handled = true;
        };
        inputRowContent.Children.Add(send);
        inputRowContent.Children.Add(input);
        inputRow.Child = inputRowContent;
        DockPanel.SetDock(inputRow, Dock.Bottom);
        root.Children.Add(inputRow);

        output.IsReadOnly = true;
        output.AcceptsReturn = true;
        output.TextWrapping = TextWrapping.NoWrap;
        output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        output.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        output.FontFamily = new FontFamily("Consolas");
        output.FontSize = 12.5;
        output.Background = PanelBrush;
        output.Foreground = TextBrush;
        output.BorderThickness = new Thickness(0);
        output.Padding = new Thickness(12, 8, 12, 8);
        root.Children.Add(output);

        Content = root;

        this.controller.ConsoleLine += OnConsoleLine;
        Closed += (_, _) => this.controller.ConsoleLine -= OnConsoleLine;

        input.Focus();
    }

    private void OnConsoleLine(object? sender, string line)
    {
        if (Dispatcher.CheckAccess())
            Append(line);
        else
            Dispatcher.BeginInvoke(new Action(() => Append(line)));
    }

    private void Append(string line)
    {
        output.AppendText(line + Environment.NewLine);
        if (output.Text.Length > MaxCharacters)
        {
            output.Text = output.Text[^TrimToCharacters..];
            output.CaretIndex = output.Text.Length;
        }
        output.ScrollToEnd();
    }

    private void Submit()
    {
        var command = input.Text.Trim();
        input.Clear();
        if (command.Length == 0)
            return;

        Append("> " + command);
        try
        {
            controller.SendConsoleCommand(command);
        }
        catch (Exception ex)
        {
            Append("! " + ex.Message);
        }
        input.Focus();
    }
}
