namespace DeadlockMVM.Core.Models;

/// <summary>The result of locating the Deadlock installation.</summary>
public sealed class GameLocation
{
    /// <summary>The Steam library folder that contains the game.</summary>
    public string? LibraryFolder { get; init; }

    /// <summary>Deadlock install directory (steamapps/common/&lt;installdir&gt;).</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>Full path to project8.exe or deadlock.exe, when found.</summary>
    public string? ExecutablePath { get; init; }

    public bool IsInstalled =>
        ExecutablePath is not null && File.Exists(ExecutablePath);
}
