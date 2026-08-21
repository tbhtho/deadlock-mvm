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
            return Process.GetProcessesByName(processNameWithoutExtension).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
