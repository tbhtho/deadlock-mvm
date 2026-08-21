using DeadlockMVM.Core.Contracts;

namespace DeadlockMVM.Core.Services;

/// <summary>Monitors Steam and Deadlock using the injected process query.</summary>
public sealed class ProcessMonitor : IProcessMonitor
{
    private readonly IProcessQuery _query;

    public ProcessMonitor(IProcessQuery query) => _query = query;

    public bool IsSteamRunning { get; private set; }

    public bool IsDeadlockRunning { get; private set; }

    public void Refresh()
    {
        IsSteamRunning = _query.IsRunning(DeadlockConstants.SteamProcessName);
        IsDeadlockRunning = _query.IsRunning(DeadlockConstants.GameProcessName)
            || _query.IsRunning(DeadlockConstants.GameProcessNameAlt);
    }
}
