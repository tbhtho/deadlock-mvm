namespace DeadlockMVM.Core.Contracts;

/// <summary>Queries whether a named process is currently running.</summary>
public interface IProcessQuery
{
    bool IsRunning(string processNameWithoutExtension);
}

/// <summary>Tracks the running state of Steam and Deadlock.</summary>
public interface IProcessMonitor
{
    bool IsSteamRunning { get; }

    bool IsDeadlockRunning { get; }

    void Refresh();
}
