using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DeadlockMVM.Launcher.Smvm;

/// <summary>
/// Low-level mouse hook that serves exactly one purpose: while the internal
/// SMVM menu explicitly owns the pointer, mouse button/wheel messages are
/// forwarded to that menu and withheld from Deadlock so one editor click
/// cannot also change spectator state.
/// </summary>
public sealed class SmvmPointerForwarder : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
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
    private readonly HookProc _mouseCallback;
    private readonly Func<bool>? _internalMenuOwnsPointer;
    private readonly Action<string>? _debugLog;
    private uint _ownedMenuPointerButtons;
    private IntPtr _ownedMenuPointerWindow;
    private NativePoint _pendingMovePoint;
    private IntPtr _pendingMoveWindow;
    private bool _pointerMoveQueued;
    private IntPtr _mouseHook;
    private bool _disposed;

    public SmvmPointerForwarder(
        Dispatcher? dispatcher = null,
        Func<bool>? internalMenuOwnsPointer = null,
        Action<string>? debugLog = null)
    {
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _mouseCallback = MouseHook;
        _internalMenuOwnsPointer = internalMenuOwnsPointer;
        _debugLog = debugLog;

        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseCallback, module, 0);
        if (_mouseHook == IntPtr.Zero)
            LastError = Marshal.GetLastWin32Error();
    }

    public bool IsAvailable => _mouseHook != IntPtr.Zero && !_disposed;
    public int LastError { get; private set; }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = unchecked((int)wParam);
            if (TryForwardOwnedMenuPointer(message, lParam))
                return (IntPtr)1;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private bool TryForwardOwnedMenuPointer(int message, IntPtr hookData)
    {
        var supported = message is WmMouseMove or WmLButtonDown or WmLButtonUp or WmRButtonDown or WmRButtonUp or
            WmMButtonDown or WmMButtonUp or WmXButtonDown or WmXButtonUp or WmMouseWheel;
        if (!supported)
            return false;
        if (_internalMenuOwnsPointer?.Invoke() != true)
        {
            _ownedMenuPointerButtons = 0;
            _ownedMenuPointerWindow = IntPtr.Zero;
            return false;
        }

        var point = new NativePoint
        {
            X = Marshal.ReadInt32(hookData),
            Y = Marshal.ReadInt32(hookData, sizeof(int)),
        };
        var mouseData = unchecked((uint)Marshal.ReadInt32(hookData, sizeof(int) * 2));
        if (message == WmMouseMove)
        {
            var ownedWindow = _ownedMenuPointerWindow;
            if ((_ownedMenuPointerButtons & 1u) == 0 || ownedWindow == IntPtr.Zero)
                return false;
            QueueOwnedMenuPointerMotion(ownedWindow, point);
            return true;
        }

        if (TryGetPointerButton(message, mouseData, out var buttonMask, out var downMessage,
                out var isDown))
        {
            if (isDown)
            {
                if (!TryGetForegroundDeadlockWindow(out var foregroundWindow))
                {
                    _debugLog?.Invoke($"SMVM pointer: msg={message:X} skipped (deadlock not foreground)");
                    return false;
                }

                _ownedMenuPointerWindow = foregroundWindow;
                _ownedMenuPointerButtons |= buttonMask;
                if (buttonMask == 1u)
                    QueueOwnedMenuPointerMessage(foregroundWindow, WmLButtonDown, point, mouseData);
                return true;
            }

            var ownedPress = (_ownedMenuPointerButtons & buttonMask) != 0;
            var ownedWindow = _ownedMenuPointerWindow;
            _ownedMenuPointerButtons &= ~buttonMask;
            if (_ownedMenuPointerButtons == 0)
                _ownedMenuPointerWindow = IntPtr.Zero;
            if (!ownedPress)
                return false;
            if (ownedWindow == IntPtr.Zero)
                return true;

            if (buttonMask == 1u)
            {
                QueueOwnedMenuPointerMessage(ownedWindow, WmLButtonUp, point, mouseData);
                return true;
            }
            QueueOwnedMenuPointerActivation(ownedWindow, downMessage, point, mouseData);
            return true;
        }

        if (!TryGetForegroundDeadlockWindow(out var wheelWindow))
        {
            _debugLog?.Invoke($"SMVM pointer: msg={message:X} skipped (deadlock not foreground)");
            return false;
        }

        // The hook only captures and schedules. Cross-process validation and
        // cursor-isolation refresh run later on the dispatcher so Windows can
        // never silently remove this low-level hook for blocking too long.
        QueueOwnedMenuPointerMessage(wheelWindow, message, point, mouseData);
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
                expectedWindow, downMessage, screenPoint, mouseData, _debugLog);
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
                expectedWindow, message, screenPoint, mouseData, _debugLog);
        });

    private void QueueOwnedMenuPointerMotion(IntPtr expectedWindow, NativePoint screenPoint)
    {
        _pendingMoveWindow = expectedWindow;
        _pendingMovePoint = screenPoint;
        if (_pointerMoveQueued)
            return;
        _pointerMoveQueued = true;
        _dispatcher.BeginInvoke(() =>
        {
            _pointerMoveQueued = false;
            var window = _pendingMoveWindow;
            var point = _pendingMovePoint;
            if (!CanForwardOwnedMenuPointer(window))
                return;
            _ = ForwardOwnedMenuPointerAction(
                window, WmMouseMove, point, 0, _debugLog);
        });
    }

    private bool CanForwardOwnedMenuPointer(IntPtr expectedWindow) =>
        !_disposed && _internalMenuOwnsPointer?.Invoke() == true &&
        TryGetForegroundDeadlockWindow(out var foregroundWindow) &&
        foregroundWindow == expectedWindow;

    private static bool ForwardOwnedMenuPointerAction(
        IntPtr window,
        int message,
        NativePoint screenPoint,
        uint mouseData,
        Action<string>? debugLog = null)
    {
        var action = message switch
        {
            WmLButtonDown => 8u,
            WmLButtonUp => 9u,
            WmMouseMove => 10u,
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
        debugLog?.Invoke($"SMVM pointer: forward action={action} sent={sent != IntPtr.Zero} result={result}");
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

    internal static bool IsAllowedForegroundProcess(string? processName) =>
        string.Equals(processName, "deadlock", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(processName, "project8", StringComparison.OrdinalIgnoreCase);

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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ownedMenuPointerButtons = 0;
        _ownedMenuPointerWindow = IntPtr.Zero;
        _pointerMoveQueued = false;
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
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
