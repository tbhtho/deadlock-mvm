using System.Diagnostics;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Handles demo file discovery and basic file operations. It deliberately does
/// not inspect the contents of .dem files; parsing can be added later.
/// </summary>
public sealed class ReplayService : IReplayService
{
    private const string ReplayDirectoryRelativePath = @"game\citadel\replays";
    private const string CitadelDirectoryName = "citadel";

    public string? GetReplayDirectory(string? deadlockExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(deadlockExecutablePath))
            return null;

        try
        {
            var executablePath = Path.GetFullPath(deadlockExecutablePath);
            var win64Directory = Directory.GetParent(executablePath);
            var binDirectory = win64Directory?.Parent;
            var gameDirectory = binDirectory?.Parent;
            var installDirectory = gameDirectory?.Parent;

            if (win64Directory is null
                || binDirectory is null
                || gameDirectory is null
                || installDirectory is null
                || !string.Equals(win64Directory.Name, "win64", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(binDirectory.Name, "bin", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(gameDirectory.Name, "game", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Path.Combine(installDirectory.FullName, ReplayDirectoryRelativePath);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<ReplayInfo> ScanReplays(string? deadlockExecutablePath)
    {
        var replayDirectory = GetReplayDirectory(deadlockExecutablePath);
        if (replayDirectory is null)
            return Array.Empty<ReplayInfo>();

        try
        {
            Directory.CreateDirectory(replayDirectory);

            return Directory.EnumerateFiles(replayDirectory, "*.dem", SearchOption.TopDirectoryOnly)
                .Select(CreateReplayInfo)
                .Where(replay => replay is not null)
                .Select(replay => replay!)
                .OrderByDescending(replay => replay.ModifiedDate)
                .ToList();
        }
        catch
        {
            return Array.Empty<ReplayInfo>();
        }
    }

    public ReplayImportResult ImportReplay(string sourcePath, string? deadlockExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return ReplayImportResult.Failed("The selected demo file could not be found.");

        if (!string.Equals(Path.GetExtension(sourcePath), ".dem", StringComparison.OrdinalIgnoreCase))
            return ReplayImportResult.Failed("Only .dem demo files can be imported.");

        var replayDirectory = GetReplayDirectory(deadlockExecutablePath);
        if (replayDirectory is null)
            return ReplayImportResult.Failed("Deadlock's replay directory could not be located.");

        try
        {
            Directory.CreateDirectory(replayDirectory);

            var destinationPath = GetAvailableDestinationPath(
                replayDirectory,
                Path.GetFileName(sourcePath));

            File.Copy(sourcePath, destinationPath, overwrite: false);

            var replay = CreateReplayInfo(destinationPath);
            return replay is null
                ? ReplayImportResult.Failed("The demo was copied but could not be read afterward.")
                : ReplayImportResult.Imported(replay);
        }
        catch (Exception ex)
        {
            return ReplayImportResult.Failed($"Could not import the demo: {ex.Message}");
        }
    }

    public bool OpenReplayDirectory(string? deadlockExecutablePath)
    {
        var replayDirectory = GetReplayDirectory(deadlockExecutablePath);
        if (replayDirectory is null)
            return false;

        try
        {
            Directory.CreateDirectory(replayDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = replayDirectory,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string GetReplayGamePath(ReplayInfo replay)
    {
        if (!string.IsNullOrWhiteSpace(replay.GamePath))
            return replay.GamePath;

        return ComputeGamePath(replay.FullPath);
    }

    private static ReplayInfo? CreateReplayInfo(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return !file.Exists
                ? null
                : new ReplayInfo
                {
                    FileName = file.Name,
                    FullPath = file.FullName,
                    GamePath = ComputeGamePath(file.FullName),
                    FileSize = file.Length,
                    ModifiedDate = file.LastWriteTime,
                };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Converts a filesystem path into the engine-relative demo path:
    /// "&lt;citadel&gt;\replays\foo.dem" becomes "replays/foo" (no extension).
    /// </summary>
    private static string ComputeGamePath(string fullPath)
    {
        try
        {
            var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(fullPath))!);
            while (current is not null
                && !string.Equals(current.Name, CitadelDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                current = current.Parent;
            }

            if (current is not null)
            {
                var citadelRoot = current.FullName.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                var relative = Path.GetFullPath(fullPath)
                    .Substring(citadelRoot.Length)
                    .Replace('\\', '/');

                if (relative.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
                    relative = relative[..^4];

                return relative;
            }
        }
        catch
        {
            // Fall through to the simple form below.
        }

        return $"replays/{Path.GetFileNameWithoutExtension(fullPath)}";
    }

    private static string GetAvailableDestinationPath(string replayDirectory, string fileName)
    {
        var originalPath = Path.Combine(replayDirectory, fileName);
        if (!File.Exists(originalPath))
            return originalPath;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var suffix = 1;

        while (true)
        {
            var candidate = Path.Combine(replayDirectory, $"{baseName} ({suffix}){extension}");
            if (!File.Exists(candidate))
                return candidate;

            suffix++;
        }
    }
}
