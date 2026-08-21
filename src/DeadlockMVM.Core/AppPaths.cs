namespace DeadlockMVM.Core;

/// <summary>
/// Central location for the application's on-disk state (logs and settings).
/// Uses %LOCALAPPDATA% so it works without admin rights.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeadlockMVM");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string LogFile => Path.Combine(LogsDirectory, "launcher.log");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
}
