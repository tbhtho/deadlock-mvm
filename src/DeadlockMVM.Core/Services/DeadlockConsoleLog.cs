using System.Text.RegularExpressions;

namespace DeadlockMVM.Core.Services;

public enum ReplayPlaybackStatus { Loading, Playing, Failed }

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
        DateTime startedAfterLocal) =>
        ReadPlaybackStatus(consoleLogPath, gamePath, startedAfterLocal) == ReplayPlaybackStatus.Playing;

    public static ReplayPlaybackStatus ReadPlaybackStatus(
        string? consoleLogPath,
        string gamePath,
        DateTime startedAfterLocal)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(consoleLogPath) || string.IsNullOrWhiteSpace(gamePath))
                return ReplayPlaybackStatus.Loading;
            if (!File.Exists(consoleLogPath))
                return ReplayPlaybackStatus.Loading;
            if (File.GetLastWriteTime(consoleLogPath) < startedAfterLocal)
                return ReplayPlaybackStatus.Loading;

            using var stream = new FileStream(
                consoleLogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var requested = false;
            var status = ReplayPlaybackStatus.Loading;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Contains("Requesting playback of", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("playing demo from", StringComparison.OrdinalIgnoreCase))
                {
                    requested = line.Contains(gamePath + ".dem", StringComparison.OrdinalIgnoreCase);
                    status = ReplayPlaybackStatus.Loading;
                    continue;
                }
                if (!requested)
                    continue;

                if (line.Contains("Disconnection during connection phase", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Error processing network message", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Demo playback finished", StringComparison.OrdinalIgnoreCase))
                {
                    status = ReplayPlaybackStatus.Failed;
                    requested = false;
                }
                else if ((Regex.IsMatch(line, @"Currently playing [1-9]\d* of \d+ ticks")
                          && line.Contains("File:" + gamePath + ".dem", StringComparison.OrdinalIgnoreCase))
                         || line.Contains("Demo paused at engine time", StringComparison.OrdinalIgnoreCase)
                         || line.Contains("CGameRules - paused on tick", StringComparison.OrdinalIgnoreCase))
                {
                    status = ReplayPlaybackStatus.Playing;
                }
            }

            return status;
        }
        catch
        {
            return ReplayPlaybackStatus.Loading;
        }
    }
}
