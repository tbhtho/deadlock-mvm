using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Launcher.ViewModels;
namespace DeadlockMVM.Launcher;

public sealed class KeyboardBindingsWindow : Window
{
    private readonly MainViewModel vm;
    private readonly ComboBox actions = new();
    private readonly TextBlock current = new() { Margin = new Thickness(0, 10, 0, 10) };
    private readonly CheckBox ctrl = new() { Content = "Ctrl chord" }, alt = new() { Content = "Alt chord" }, shift = new() { Content = "Shift chord" }, win = new() { Content = "Win chord" };
    private readonly List<(Button Button, uint Code, bool Mouse)> keys = new();
    private static readonly (string Label, string Property)[] Bindings = [
        ("Move forward", "SmvmForwardHotkey"),
        ("Move backward", "SmvmBackHotkey"),
        ("Move left", "SmvmLeftHotkey"),
        ("Move right", "SmvmRightHotkey"),
        ("Move up", "SmvmUpHotkey"),
        ("Move down", "SmvmDownHotkey"),
        ("Fast movement", "SmvmFastHotkey"),
        ("Precision movement", "SmvmPrecisionHotkey"),
        ("Roll left", "SmvmRollLeftHotkey"),
        ("Roll right", "SmvmRollRightHotkey"),
        ("Reset roll", "SmvmRollResetHotkey"),
        ("Main menu", "SmvmMenuHotkey"),
        ("Add keyframe", "SmvmAddHotkey"),
        ("Delete keyframe", "SmvmDeleteHotkey"),
        ("Clean footage", "SmvmCleanViewHotkey"),
        ("Play path from start", "SmvmPlayStartHotkey"),
        ("Play path from current", "SmvmPlayCurrentHotkey"),
        ("Stop path", "SmvmStopHotkey"),
        ("Undo", "SmvmUndoHotkey"),
        ("Redo", "SmvmRedoHotkey"),
        ("Show path", "SmvmShowPathHotkey"),
        ("Show cameras", "SmvmShowCamerasHotkey"),
        ("Emergency restore UI", "SmvmRestoreUiHotkey"),
        ("Cycle UI", "SmvmCycleUiHotkey"),
        ("Toggle free camera", "SmvmToggleFreeCameraHotkey"),
        ("Pause replay", "SmvmReplayPauseHotkey"),
        ("Show labels", "SmvmShowLabelsHotkey"),
        ("Step back", "SmvmStepBackHotkey"),
        ("Step forward", "SmvmStepForwardHotkey"),
        ("Effects panel", "SmvmEffectsHotkey"),
        ("Start ready cinematic", "SmvmCinematicStartHotkey"),
        ("Slower replay", "SmvmPlaybackSlowerHotkey"),
        ("Faster replay", "SmvmPlaybackFasterHotkey"),
        ("Cancel recording / camera", "SmvmCancelHotkey"),
        ("Slower camera", "SmvmCameraSlowerHotkey"),
        ("Faster camera", "SmvmCameraFasterHotkey"),
    ];
    public KeyboardBindingsWindow(MainViewModel model)
    {
        vm = model; Title = "Keyboard + Mouse - Deadlock MVM";
        Width = 1060; Height = Math.Min(850, SystemParameters.WorkArea.Height - 60); MinWidth = 760; MinHeight = 530;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(28,30,33)); Foreground = Brushes.WhiteSmoke;
        var root = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Background = Background, Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(new TextBlock { Text = "Select an action, then click its new key. Hover highlighted keys to see assignments.", Margin = new Thickness(0,0,0,12) });
        foreach (var b in Bindings) actions.Items.Add(b.Label);
        root.Children.Add(actions); root.Children.Add(current);
        var modifiers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,12) };
        foreach(var c in new[]{ctrl,alt,shift,win}) { c.Foreground = Brushes.WhiteSmoke; c.Margin = new Thickness(0,0,20,0); modifiers.Children.Add(c); c.Click += (_,_) => Refresh(); }
        root.Children.Add(modifiers);
        var keyboard = new StackPanel();
        root.Children.Add(new Viewbox { Child = keyboard, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
        AddRow(keyboard, [("Esc", 27u, 1d),("F1", 112u, 1d),("F2", 113u, 1d),("F3", 114u, 1d),("F4", 115u, 1d),("F5", 116u, 1d),("F6", 117u, 1d),("F7", 118u, 1d),("F8", 119u, 1d),("F9", 120u, 1d),("F10", 121u, 1d),("F11", 122u, 1d),("F12", 123u, 1d)]);
        AddRow(keyboard, [("`", 192u, 1d),("1", 49u, 1d),("2", 50u, 1d),("3", 51u, 1d),("4", 52u, 1d),("5", 53u, 1d),("6", 54u, 1d),("7", 55u, 1d),("8", 56u, 1d),("9", 57u, 1d),("0", 48u, 1d),("-", 189u, 1d),("=", 187u, 1d),("Back", 8u, 1.8d)]);
        AddRow(keyboard, [("Tab", 9u, 1.5d),("Q", 81u, 1d),("W", 87u, 1d),("E", 69u, 1d),("R", 82u, 1d),("T", 84u, 1d),("Y", 89u, 1d),("U", 85u, 1d),("I", 73u, 1d),("O", 79u, 1d),("P", 80u, 1d),("[", 219u, 1d),("]", 221u, 1d),("\\", 220u, 1.3d)]);
        AddRow(keyboard, [("Caps", 20u, 1.8d),("A", 65u, 1d),("S", 83u, 1d),("D", 68u, 1d),("F", 70u, 1d),("G", 71u, 1d),("H", 72u, 1d),("J", 74u, 1d),("K", 75u, 1d),("L", 76u, 1d),(";", 186u, 1d),("'", 222u, 1d),("Enter", 13u, 2d)]);
        AddRow(keyboard, [("Shift", 160u, 2.3d),("Z", 90u, 1d),("X", 88u, 1d),("C", 67u, 1d),("V", 86u, 1d),("B", 66u, 1d),("N", 78u, 1d),("M", 77u, 1d),(",", 188u, 1d),(".", 190u, 1d),("/", 191u, 1d),("RShift", 161u, 2.5d)]);
        AddRow(keyboard, [("Ctrl", 162u, 1.5d),("Win", 91u, 1d),("Alt", 164u, 1.5d),("Space", 32u, 6d),("RAlt", 165u, 1.5d),("RCtrl", 163u, 1.5d)]);
        AddRow(keyboard, [("Ins", 45u, 1d),("Home", 36u, 1d),("PgUp", 33u, 1d),("Del", 46u, 1d),("End", 35u, 1d),("PgDn", 34u, 1d),("Left", 37u, 1d),("Up", 38u, 1d),("Down", 40u, 1d),("Right", 39u, 1d)]);
        var mouseRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,16,0,12) };
        root.Children.Add(mouseRow);
        var mouse = new StackPanel { Margin = new Thickness(10) };
        mouseRow.Children.Add(new Border { Child = mouse, CornerRadius = new CornerRadius(40), Background = new SolidColorBrush(Color.FromRgb(42,44,48)), Padding = new Thickness(5,10,5,15) });
        var top = new StackPanel { Orientation = Orientation.Horizontal }; mouse.Children.Add(top);
        AddKey(top, "Left", 1, 1.4, true, false); AddKey(top, "Middle", 3, 1.4, true); AddKey(top, "Right", 2, 1.4, true, false);
        var mid = new StackPanel { Orientation = Orientation.Horizontal }; mouse.Children.Add(mid);
        AddKey(mid, "Side 1", 4, 1.4, true); AddKey(mid, "Wheel up", 6, 2.9, true);
        var lower = new StackPanel { Orientation = Orientation.Horizontal }; mouse.Children.Add(lower);
        AddKey(lower, "Side 2", 5, 1.4, true); AddKey(lower, "Wheel down", 7, 2.9, true);
        var entry = new StackPanel { Margin = new Thickness(24,4,0,0), Width = 350 };
        mouseRow.Children.Add(entry);
        entry.Children.Add(new TextBlock { Text = "Other keys / exact chord", Margin = new Thickness(0,0,0,8) });
        var custom = new TextBox { ToolTip = "For example Ctrl+F13, NumPad0 or Mouse3" }; entry.Children.Add(custom);
        var apply = new Button { Content = "Apply typed binding", Margin = new Thickness(0,8,0,0) };
        apply.Click += (_,_) => Assign(custom.Text); entry.Children.Add(apply);
        root.Children.Add(new TextBlock { Text = "Left and right mouse retain UI and camera-look gestures. Actions requiring keyboard input reject mouse assignments.", TextWrapping = TextWrapping.Wrap });
        var clear = new Button { Content = "Clear selected binding", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,12,0,8), Padding = new Thickness(12,6,12,6) };
        clear.Click += (_,_) => Assign(""); root.Children.Add(clear);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("StatusMessage") { Source = vm }); root.Children.Add(status);
        actions.SelectionChanged += (_,_) => Refresh(); actions.SelectedIndex = 0;
    }
    private InputModifiers Modifiers => (ctrl.IsChecked == true ? InputModifiers.Control : 0) | (alt.IsChecked == true ? InputModifiers.Alt : 0) | (shift.IsChecked == true ? InputModifiers.Shift : 0) | (win.IsChecked == true ? InputModifiers.Windows : 0);
    private string Read(string property) => (string?)typeof(MainViewModel).GetProperty(property)!.GetValue(vm) ?? "";
    private void Assign(string value) { typeof(MainViewModel).GetProperty(Bindings[actions.SelectedIndex].Property)!.SetValue(vm, value); Refresh(); }
    private void AddRow(StackPanel parent, (string Label,uint Code,double Width)[] row) {
        var panel = new StackPanel { Orientation = Orientation.Horizontal }; parent.Children.Add(panel);
        foreach(var k in row) AddKey(panel,k.Label,k.Code,k.Width,false);
    }
    private void AddKey(StackPanel panel,string label,uint code,double width,bool mouse,bool enabled=true) {
        var button = new Button { Content = label, Width = 54 * width, Height = 42, Margin = new Thickness(2), IsEnabled = enabled };
        button.Click += (_,_) => Assign(new InputBinding(mouse ? InputBindingKind.Mouse : InputBindingKind.Keyboard,code,Modifiers).ToString());
        panel.Children.Add(button); if(enabled) keys.Add((button,code,mouse));
    }
    private void Refresh() {
        if(actions.SelectedIndex < 0) return;
        current.Text = "Current: " + (Read(Bindings[actions.SelectedIndex].Property) is { Length: >0 } value ? value : "Unbound");
        foreach(var k in keys) {
            var binding = new InputBinding(k.Mouse ? InputBindingKind.Mouse : InputBindingKind.Keyboard,k.Code,Modifiers);
            var assigned = Bindings.Where(b => InputBinding.TryParse(Read(b.Property),out var other) && other == binding).Select(b=>b.Label).ToArray();
            k.Button.Background = new SolidColorBrush(assigned.Length > 0 ? Color.FromRgb(65,99,135) : Color.FromRgb(48,51,56));
            k.Button.Foreground = Brushes.WhiteSmoke; k.Button.ToolTip = binding + "\n" + (assigned.Length > 0 ? string.Join("\n",assigned) : "Unassigned");
        }
    }
}
