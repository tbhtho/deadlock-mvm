namespace DeadlockMVM.Core;

/// <summary>
/// Static facts about Deadlock and its Steam integration. Kept in one place so the
/// launcher and any future backend share a single source of truth.
/// </summary>
public static class DeadlockConstants
{
    /// <summary>Steam AppID for Deadlock.</summary>
    public const uint AppId = 1422450;

    /// <summary>Steam AppID as the string used in filenames and manifest keys.</summary>
    public const string AppIdString = "1422450";

    /// <summary>Process name (no extension) of the Steam client.</summary>
    public const string SteamProcessName = "steam";

    /// <summary>Primary Deadlock process name (development codename, no extension).</summary>
    public const string GameProcessName = "project8";

    /// <summary>Legacy/alternate Deadlock process name, checked as a fallback.</summary>
    public const string GameProcessNameAlt = "deadlock";

    /// <summary>Deadlock executable file name.</summary>
    public const string GameExecutableName = "project8.exe";

    /// <summary>Deadlock install folder name inside steamapps/common.</summary>
    public const string GameInstallDirName = "Deadlock";

    /// <summary>Path of the game binary relative to the install directory.</summary>
    public const string GameExecutableRelativePath = @"game\bin\win64";

    /// <summary>
    /// Arguments that define "Movie Mode". <c>-insecure</c> disables VAC so the
    /// game can be driven by external tooling; the rest enable developer features.
    /// </summary>
    public static readonly IReadOnlyList<string> MovieModeArguments =
        new[] { "-insecure", "-dev", "-console", "-condebug" };
}
