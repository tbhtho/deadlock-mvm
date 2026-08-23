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
/// Foreground-scoped keyboard/mouse bindings. Low-level hooks normally observe
/// without suppressing input. While the internal SMVM menu explicitly owns the
/// pointer, mouse button/wheel messages are forwarded to that menu and withheld
/// from Deadlock so one editor click cannot also change spectator state.
/// </summary>
public sealed class CampathHotkeyService : IHotkeyService
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const uint WmSmvmCursorTransition = 0x84D0;
    private const uint WmSmvmPointerAction = 0x84D2;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint PointerTransitionTimeoutMs = 100;

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<HotkeyAction, InputBinding> _bindings = [];
    private readonly HashSet<uint> _downKeys = [];
    private readonly HookProc _keyboardCallback;
    private readonly HookProc _mouseCallback;
    private readonly InputBinding? _reservedDirectorBinding;
    private readonly Func<bool>? _internalMenuOwnsPointer;
    private uint _ownedMenuPointerButtons;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private HotkeyAction? _captureAction;
    private bool _disposed;

    public CampathHotkeyService(
        string directorBinding,
        Dispatcher? dispatcher = null,
        Func<bool>? internalMenuOwnsPointer = null)
    {
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _keyboardCallback = KeyboardHook;
        _mouseCallback = MouseHook;
        _internalMenuOwnsPointer = internalMenuOwnsPointer;
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
            if (TryForwardOwnedMenuPointer(message, lParam))
                return (IntPtr)1;

            var mouseData = unchecked((uint)Marshal.ReadInt32(lParam, 8));
            uint button = message switch
            {
                WmMButtonDown => 3,
                WmXButtonDown => (mouseData >> 16) == 1 ? 4u : 5u,
                WmMouseWheel => unchecked((short)(mouseData >> 16)) > 0 ? 6u : 7u,
                _ => 0,
            };
            if (button != 0)
                QueueInput(new InputBinding(InputBindingKind.Mouse, button, ReadModifiers()));
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private bool TryForwardOwnedMenuPointer(int message, IntPtr hookData)
    {
        var supported = message is WmLButtonDown or WmLButtonUp or WmRButtonDown or WmRButtonUp or
            WmMButtonDown or WmMButtonUp or WmXButtonDown or WmXButtonUp or WmMouseWheel;
        if (!supported)
            return false;
        if (!TryGetForegroundDeadlockWindow(out var window))
            return false;
        if (_internalMenuOwnsPointer?.Invoke() != true)
        {
            _ownedMenuPointerButtons = 0;
            return false;
        }

        var point = new NativePoint
        {
            X = Marshal.ReadInt32(hookData),
            Y = Marshal.ReadInt32(hookData, sizeof(int)),
        };
        var pointInClient = ValidateOwnedMenuPointer(window, point);
        var mouseData = unchecked((uint)Marshal.ReadInt32(hookData, sizeof(int) * 2));

        if (TryGetPointerButton(message, mouseData, out var buttonMask, out var downMessage,
                out var isDown))
        {
            if (isDown)
            {
                if (!pointInClient)
                    return false;
                if (!RefreshInternalMenuPointerIsolation(window))
                    return true;

                _ownedMenuPointerButtons |= buttonMask;
                return true;
            }

            var ownedPress = (_ownedMenuPointerButtons & buttonMask) != 0;
            _ownedMenuPointerButtons &= ~buttonMask;
            if (!ownedPress)
                return false;
            if (!pointInClient)
                return true;
            if (!RefreshInternalMenuPointerIsolation(window))
                return true;

            QueueOwnedMenuPointerActivation(window, downMessage, point, mouseData);
            return true;
        }

        if (!pointInClient)
            return false;
        if (!RefreshInternalMenuPointerIsolation(window))
        {
            // Do not synthesize an editor click unless the in-process window
            // thread has first withdrawn any raw mouse registration that a
            // spectator-mode transition may have re-enabled. The physical
            // low-level event is still withheld from the normal message path.
            return true;
        }

        QueueOwnedMenuPointerMessage(window, message, point, mouseData);
        return true;
    }

    private void QueueOwnedMenuPointerActivation(
        IntPtr expectedWindow,
        int downMessage,
        NativePoint screenPoint,
        uint mouseData) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (!CanForwardOwnedMenuPointer(expectedWindow))
                return;
            if (!RefreshInternalMenuPointerIsolation(expectedWindow))
                return;

            _ = ForwardOwnedMenuPointerAction(
                expectedWindow, downMessage, screenPoint, mouseData);
        });

    private void QueueOwnedMenuPointerMessage(
        IntPtr expectedWindow,
        int message,
        NativePoint screenPoint,
        uint mouseData) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (!CanForwardOwnedMenuPointer(expectedWindow))
                return;
            if (!RefreshInternalMenuPointerIsolation(expectedWindow))
                return;

            _ = ForwardOwnedMenuPointerAction(
                expectedWindow, message, screenPoint, mouseData);
        });

    private bool CanForwardOwnedMenuPointer(IntPtr expectedWindow) =>
        !_disposed && _internalMenuOwnsPointer?.Invoke() == true &&
        TryGetForegroundDeadlockWindow(out var foregroundWindow) &&
        foregroundWindow == expectedWindow;

    private static bool ForwardOwnedMenuPointerAction(
        IntPtr window,
        int message,
        NativePoint screenPoint,
        uint mouseData)
    {
        var action = message switch
        {
            WmLButtonDown => 1u,
            WmRButtonDown => 2u,
            WmMButtonDown => 3u,
            WmXButtonDown when (mouseData >> 16) == 1 => 4u,
            WmXButtonDown => 5u,
            WmMouseWheel => 6u,
            _ => 0u,
        };
        if (action == 0)
            return false;
        var data = message == WmMouseWheel ? unchecked((ushort)(mouseData >> 16)) : (ushort)0;
        var packedAction = action | ((nuint)data << 16);
        var sent = SendMessageTimeout(
            window,
            WmSmvmPointerAction,
            (IntPtr)packedAction,
            PackPoint(screenPoint),
            SmtoBlock | SmtoAbortIfHung,
            PointerTransitionTimeoutMs,
            out var result);
        return sent != IntPtr.Zero && result != IntPtr.Zero;
    }

    private static bool TryGetPointerButton(
        int message,
        uint mouseData,
        out uint buttonMask,
        out int downMessage,
        out bool isDown)
    {
        (buttonMask, downMessage, isDown) = message switch
        {
            WmLButtonDown => (1u, WmLButtonDown, true),
            WmLButtonUp => (1u, WmLButtonDown, false),
            WmRButtonDown => (2u, WmRButtonDown, true),
            WmRButtonUp => (2u, WmRButtonDown, false),
            WmMButtonDown => (4u, WmMButtonDown, true),
            WmMButtonUp => (4u, WmMButtonDown, false),
            WmXButtonDown when (mouseData >> 16) == 1 =>
                (8u, WmXButtonDown, true),
            WmXButtonUp when (mouseData >> 16) == 1 =>
                (8u, WmXButtonDown, false),
            WmXButtonDown => (16u, WmXButtonDown, true),
            WmXButtonUp => (16u, WmXButtonDown, false),
            _ => (0u, 0, false),
        };
        return buttonMask != 0;
    }

    private static bool RefreshInternalMenuPointerIsolation(IntPtr window)
    {
        var sent = SendMessageTimeout(
            window,
            WmSmvmCursorTransition,
            (IntPtr)1,
            IntPtr.Zero,
            SmtoBlock | SmtoAbortIfHung,
            PointerTransitionTimeoutMs,
            out var result);
        return sent != IntPtr.Zero && result != IntPtr.Zero;
    }

    private static IntPtr PackPoint(NativePoint point) =>
        (IntPtr)unchecked((nint)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16)));

    private static bool ValidateOwnedMenuPointer(IntPtr window, NativePoint screenPoint)
    {
        const nuint validateAction = 7;
        var sent = SendMessageTimeout(
            window,
            WmSmvmPointerAction,
            (IntPtr)validateAction,
            PackPoint(screenPoint),
            SmtoBlock | SmtoAbortIfHung,
            PointerTransitionTimeoutMs,
            out var result);
        return sent != IntPtr.Zero && result != IntPtr.Zero;
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

    private static bool IsDeadlockForeground() => TryGetForegroundDeadlockWindow(out _);

    private static bool TryGetForegroundDeadlockWindow(out IntPtr window)
    {
        window = GetForegroundWindow();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
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
        _ownedMenuPointerButtons = 0;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeoutMilliseconds,
        out IntPtr result);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
