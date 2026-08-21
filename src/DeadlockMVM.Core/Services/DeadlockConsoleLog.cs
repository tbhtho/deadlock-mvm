namespace DeadlockMVM.Core.Services;

/// <summary>
/// Read access to Deadlock's <c>-condebug</c> console log, used to verify that
/// a requested demo actually started playing. The log is truncated by the game
/// on every launch, so callers must scan the whole file rather than offsets.
/// </summary>
public static class DeadlockConsoleLog
{
    private const string ConsoleLogRelativePath = @"game\citadel\console.log";

    /// <summary>Derives the console log path from a Deadlock executable path.</summary>
    public static string? GetConsoleLogPath(string? deadlockExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(deadlockExecutablePath))
            return null;

        try
        {
            var executablePath = Path.GetFullPath(deadlockExecutablePath);
            var win64Directory = Directory.GetParent(executablePath);
            var binDirectory = win64Directory?.Parent;
            var gameDirectory = binDirectory?.Parent;

            if (win64Directory is null
                || binDirectory is null
                || gameDirectory is null
                || !string.Equals(win64Directory.Name, "win64", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(binDirectory.Name, "bin", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(gameDirectory.Name, "game", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Path.Combine(gameDirectory.FullName, "citadel", "console.log");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true when the console log written after <paramref name="startedAfterLocal"/>
    /// contains an engine confirmation that the given game path started playing.
    /// The game keeps the log open for writing while running, so the file must be
    /// opened with full share permissions.
    /// </summary>
    public static bool ContainsPlaybackConfirmation(
        string? consoleLogPath,
        string gamePath,
        DateTime startedAfterLocal)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(consoleLogPath) || string.IsNullOrWhiteSpace(gamePath))
                return false;
            if (!File.Exists(consoleLogPath))
                return false;
            if (File.GetLastWriteTime(consoleLogPath) < startedAfterLocal)
                return false;

            using var stream = new FileStream(
                consoleLogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.IndexOf(gamePath, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (line.IndexOf("Requesting playback of", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("playing demo from", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
