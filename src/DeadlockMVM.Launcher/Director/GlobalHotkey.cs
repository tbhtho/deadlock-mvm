using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Input;

namespace DeadlockMVM.Launcher.Director;

/// <summary>
/// Registers a global show/hide hotkey for the Director window.
///
/// The hotkey is registered against the Director window's native handle,
/// which is force-created via <see cref="WindowInteropHelper.EnsureHandle"/>
/// so registration works while the window is still hidden. WM_HOTKEY is
/// received through an <see cref="HwndSource"/> hook.
///
/// Registration can transiently fail when another process briefly owns the
/// combo; call <see cref="TryRegister"/> to retry (the Director does this on
/// a timer) until it sticks.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;

    private readonly Action _callback;
    private readonly HwndSource? _source;
    private readonly IntPtr _hwnd;
    private readonly uint _id = 0xDEAD0001;
    private readonly uint _mods;
    private readonly uint _vk;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkey(System.Windows.Window window, string keyString, Action callback)
    {
        KeyName = keyString;
        _callback = callback;

        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _hwnd = helper.Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        if (TryParse(keyString, out _mods, out _vk))
            TryRegister();
        else
            LastError = 87; // ERROR_INVALID_PARAMETER
    }

    /// <summary>Human-readable combo this instance tries to register.</summary>
    public string KeyName { get; }

    /// <summary>Last Win32 error from a failed registration attempt.</summary>
    public int LastError { get; private set; }

    /// <summary>False while the combo could not be registered (yet).</summary>
    public bool IsRegistered => _registered;

    /// <summary>Attempts registration again; safe to call repeatedly.</summary>
    public bool TryRegister()
    {
        if (_registered || _disposed || _hwnd == IntPtr.Zero || _vk == 0)
            return _registered;

        _registered = RegisterHotKey(_hwnd, _id, _mods, _vk);
        if (!_registered)
            LastError = Marshal.GetLastWin32Error();

        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && unchecked((uint)wParam.ToInt32()) == _id)
        {
            handled = true;
            _callback();
        }

        return IntPtr.Zero;
    }

    private static bool TryParse(string s, out uint mods, out uint vk)
    {
        mods = 0x4000; // MOD_NOREPEAT
        vk = 0;
        Key key = Key.None;
        try
        {
            foreach (var part in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": mods |= 0x0002; break;   // MOD_CONTROL
                    case "alt": mods |= 0x0001; break;    // MOD_ALT
                    case "shift": mods |= 0x0004; break;  // MOD_SHIFT
                    case "win": mods |= 0x0008; break;    // MOD_WIN
                    default:
                        if (!Enum.TryParse(part, true, out key))
                            return false;
                        break;
                }
            }
        }
        catch (ArgumentException)
        {
            mods = 0;
            vk = 0;
            return false;
        }

        vk = key == Key.None ? 0u : (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_registered && _hwnd != IntPtr.Zero)
            UnregisterHotKey(_hwnd, _id);
        _source?.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, uint id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, uint id);
}
