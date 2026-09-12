using System.Diagnostics;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Fast, deliberate force-exit for a running Deadlock process. Steam owns the
/// launched process, so the game is located by process name rather than a
/// retained handle.
/// </summary>
public static class DeadlockProcessControl
{
    /// <summary>
    /// Immediately terminates every running Deadlock process. Returns the
    /// number terminated; <paramref name="error"/> carries the last failure
    /// when a process could not be killed.
    /// </summary>
    public static int ForceExit(out string? error)
    {
        var terminated = 0;
        error = null;
        foreach (var processName in new[]
                 {
                     DeadlockConstants.GameProcessName,
                     DeadlockConstants.GameProcessNameAlt,
                 })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill();
                    terminated++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                                           System.ComponentModel.Win32Exception or
                                           NotSupportedException)
                {
                    error = ex.Message;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return terminated;
    }
}
