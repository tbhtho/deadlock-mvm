using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Contracts;

/// <summary>Locates Steam and the Deadlock installation.</summary>
public interface ISteamService
{
    /// <summary>Detects the Steam installation (registry + libraryfolders.vdf).</summary>
    SteamLocation DetectSteam();

    /// <summary>Returns the library folders listed for a given Steam install.</summary>
    IReadOnlyList<string> GetLibraryFolders(string steamInstallPath);

    /// <summary>Locates the Deadlock installation across all Steam libraries.</summary>
    GameLocation? LocateGame(string steamInstallPath);

    /// <summary>
    /// Validates a user-selected Deadlock executable or install directory.
    /// </summary>
    GameLocation? ValidateGamePath(string path);
}
