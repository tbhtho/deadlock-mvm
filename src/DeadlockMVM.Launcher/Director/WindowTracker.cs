using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace DeadlockMVM.Launcher.Director;

/// <summary>
/// Finds the Deadlock main window and tracks its position so the Director
/// overlay can stay anchored to it. Checks both known process names.
/// </summary>
public sealed class WindowTracker
{
    private readonly string[] _processNames;
    private readonly System.Threading.Timer _timer;
    private IntPtr _lastHwnd;
    private Rect _lastRect;

    public event EventHandler<Rect>? WindowMoved;

    public WindowTracker(TimeSpan? pollInterval = null, params string[] processNames)
    {
        _processNames = processNames.Length > 0 ? processNames : new[] { "project8", "deadlock" };
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, pollInterval ?? TimeSpan.FromMilliseconds(500));
    }

    public Rect? CurrentRect => TryGetRect(out var r) ? r : null;

    public bool IsGameRunning => TryGetRect(out _);

    public void Dispose() => _timer.Dispose();

    private void Poll()
    {
        if (TryGetRect(out var rect))
        {
            if (_lastHwnd == IntPtr.Zero || rect != _lastRect)
            {
                _lastRect = rect;
                WindowMoved?.Invoke(this, rect);
            }
        }
    }

    private bool TryGetRect(out Rect rect)
    {
        rect = default;
        IntPtr hwnd = IntPtr.Zero;

        foreach (var name in _processNames)
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes)
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    hwnd = process.MainWindowHandle;
                    break;
                }
            }

            foreach (var process in processes)
                process.Dispose();

            if (hwnd != IntPtr.Zero)
                break;
        }

        if (hwnd == IntPtr.Zero)
        {
            _lastHwnd = IntPtr.Zero;
            return false;
        }

        if (GetWindowRect(hwnd, out var nativeRect))
        {
            rect = new Rect(nativeRect.Left, nativeRect.Top, nativeRect.Right - nativeRect.Left, nativeRect.Bottom - nativeRect.Top);
            _lastHwnd = hwnd;
            return true;
        }

        _lastHwnd = IntPtr.Zero;
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}