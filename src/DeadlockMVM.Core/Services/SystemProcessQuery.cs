using System.Diagnostics;
using DeadlockMVM.Core.Contracts;

namespace DeadlockMVM.Core.Services;

/// <summary>Real process query backed by System.Diagnostics.Process.</summary>
public sealed class SystemProcessQuery : IProcessQuery
{
    public bool IsRunning(string processNameWithoutExtension)
    {
        try
        {
            var processes = Process.GetProcessesByName(processNameWithoutExtension);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }
}
