namespace DeadlockMVM.Core.Models;

/// <summary>The result of detecting a Steam installation.</summary>
public sealed class SteamLocation
{
    /// <summary>Root folder of the Steam installation (contains steam.exe).</summary>
    public string? InstallPath { get; init; }

    /// <summary>Full path to steam.exe, when known.</summary>
    public string? ExecutablePath { get; init; }

    public bool IsInstalled =>
        ExecutablePath is not null && File.Exists(ExecutablePath);
}
