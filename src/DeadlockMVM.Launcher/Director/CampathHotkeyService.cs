using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Launcher.Director;

public sealed record HotkeyTriggeredEventArgs(HotkeyAction Action, InputBinding Binding);
public sealed record BindingCapturedEventArgs(HotkeyAction Action, InputBinding? Binding, bool Cancelled);

public interface IHotkeyService : IDisposable
{
    event EventHandler<HotkeyTriggeredEventArgs>? BindingTriggered;
    event EventHandler<BindingCapturedEventArgs>? BindingCaptured;
    bool IsAvailable { get; }
    int LastError { get; }
    bool TryRegister(HotkeyAction action, InputBinding binding, out string? error);
    void Unregister(HotkeyAction action);
    void BeginCapture(HotkeyAction action);
    void CancelCapture();
}

/// <summary>
/// Foreground-scoped, observation-only keyboard/mouse bindings. Low-level hooks
/// never suppress input and only enqueue work onto the WPF dispatcher.
/// </summary>
public sealed class CampathHotkeyService : IHotkeyService
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<HotkeyAction, InputBinding> _bindings = [];
    private readonly HashSet<uint> _downKeys = [];
    private readonly HookProc _keyboardCallback;
    private readonly HookProc _mouseCallback;
    private readonly InputBinding? _reservedDirectorBinding;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private HotkeyAction? _captureAction;
    private bool _disposed;

    public CampathHotkeyService(string directorBinding, Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _keyboardCallback = KeyboardHook;
        _mouseCallback = MouseHook;
        _reservedDirectorBinding = InputBinding.TryParse(directorBinding, out var reserved) ? reserved : null;

        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardCallback, module, 0);
        if (_keyboardHook == IntPtr.Zero)
            LastError = Marshal.GetLastWin32Error();
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseCallback, module, 0);
        if (_mouseHook == IntPtr.Zero)
            LastError = Marshal.GetLastWin32Error();
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;
        }
    }

    public event EventHandler<HotkeyTriggeredEventArgs>? BindingTriggered;
    public event EventHandler<BindingCapturedEventArgs>? BindingCaptured;

    public bool IsAvailable => _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero && !_disposed;
    public int LastError { get; private set; }

    public bool TryRegister(HotkeyAction action, InputBinding binding, out string? error)
    {
        error = null;
        if (!IsAvailable)
        {
            error = $"Input observer is unavailable (Windows error {LastError}).";
            return false;
        }
        if (!binding.IsValid)
        {
            error = "The input binding is invalid.";
            return false;
        }
        if (_reservedDirectorBinding is { } reserved && reserved == binding)
        {
            error = "That binding is already used to open the Director.";
            return false;
        }
        if (HotkeyConflictDetector.FindConflict(_bindings, action, binding) is { } conflict)
        {
            error = $"That binding is already assigned to {FormatAction(conflict)}.";
            return false;
        }
        _bindings[action] = binding;
        return true;
    }

    public void Unregister(HotkeyAction action) => _bindings.Remove(action);

    public void BeginCapture(HotkeyAction action) => _captureAction = action;

    public void CancelCapture()
    {
        if (_captureAction is not { } action)
            return;
        _captureAction = null;
        BindingCaptured?.Invoke(this, new BindingCapturedEventArgs(action, null, true));
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = unchecked((int)wParam);
            var key = unchecked((uint)Marshal.ReadInt32(lParam));
            if (message is WmKeyUp or WmSysKeyUp)
                _downKeys.Remove(key);
            else if (message is WmKeyDown or WmSysKeyDown && !IsModifierKey(key) && _downKeys.Add(key))
                QueueInput(new InputBinding(InputBindingKind.Keyboard, key, ReadModifiers()));
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = unchecked((int)wParam);
            uint button = message switch
            {
                WmMButtonDown => 3,
                WmXButtonDown => unchecked((uint)((Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF)) == 1 ? 4u : 5u,
                _ => 0,
            };
            if (button != 0)
                QueueInput(new InputBinding(InputBindingKind.Mouse, button, ReadModifiers()));
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void QueueInput(InputBinding binding) => _dispatcher.BeginInvoke(() => HandleInput(binding));

    private void HandleInput(InputBinding binding)
    {
        if (_captureAction is { } capture)
        {
            _captureAction = null;
            if (binding.Kind == InputBindingKind.Keyboard && binding.Code == 0x1B)
            {
                BindingCaptured?.Invoke(this, new BindingCapturedEventArgs(capture, null, true));
                return;
            }
            BindingCaptured?.Invoke(this, new BindingCapturedEventArgs(capture, binding, false));
            return;
        }

        if (!IsDeadlockForeground())
            return;
        foreach (var pair in _bindings)
        {
            if (pair.Value == binding)
            {
                BindingTriggered?.Invoke(this, new HotkeyTriggeredEventArgs(pair.Key, binding));
                return;
            }
        }
    }

    private static InputModifiers ReadModifiers()
    {
        var modifiers = InputModifiers.None;
        if (IsPressed(0x11)) modifiers |= InputModifiers.Control;
        if (IsPressed(0x12)) modifiers |= InputModifiers.Alt;
        if (IsPressed(0x10)) modifiers |= InputModifiers.Shift;
        if (IsPressed(0x5B) || IsPressed(0x5C)) modifiers |= InputModifiers.Windows;
        return modifiers;
    }

    private static bool IsPressed(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool IsModifierKey(uint key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    internal static bool IsAllowedForegroundProcess(string? processName) =>
        string.Equals(processName, "deadlock", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(processName, "project8", StringComparison.OrdinalIgnoreCase);

    private static bool IsDeadlockForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            return IsAllowedForegroundProcess(process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string FormatAction(HotkeyAction action) => action switch
    {
        HotkeyAction.CampathAddKeyframe => "Add Keyframe",
        HotkeyAction.CampathPlay => "Play Path",
        HotkeyAction.CampathStop => "Stop Path",
        _ => action.ToString(),
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
