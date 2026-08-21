using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Contracts;

/// <summary>Discovers and manages Deadlock demo files.</summary>
public interface IReplayService
{
    /// <summary>Gets the replay directory derived from a validated Deadlock executable.</summary>
    string? GetReplayDirectory(string? deadlockExecutablePath);

    /// <summary>Scans the replay directory, newest demos first.</summary>
    IReadOnlyList<ReplayInfo> ScanReplays(string? deadlockExecutablePath);

    /// <summary>Copies a .dem file into the Deadlock replay directory.</summary>
    ReplayImportResult ImportReplay(string sourcePath, string? deadlockExecutablePath);

    /// <summary>Opens the replay directory in Windows Explorer.</summary>
    bool OpenReplayDirectory(string? deadlockExecutablePath);

    /// <summary>
    /// Converts a replay's filesystem location into the engine-relative game
    /// path expected by <c>+playdemo</c> (e.g. "replays/75438101-6448").
    /// </summary>
    string GetReplayGamePath(ReplayInfo replay);
}
