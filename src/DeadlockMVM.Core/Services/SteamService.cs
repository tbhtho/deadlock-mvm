using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Vdf;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Detects the Steam installation and the Deadlock game install by reading the
/// registry, <c>libraryfolders.vdf</c> and <c>appmanifest_*.acf</c> files.
/// </summary>
public sealed class SteamService : ISteamService
{
    private static readonly IReadOnlyList<string> KnownGameExecutables =
        new[] { DeadlockConstants.GameExecutableName, "deadlock.exe" };

    private static readonly IReadOnlyList<string> DefaultFallbackPaths =
        new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" };

    private readonly IRegistryReader _registry;
    private readonly IReadOnlyList<string> _fallbackPaths;

    public SteamService(IRegistryReader? registry = null, IReadOnlyList<string>? fallbackPaths = null)
    {
        _registry = registry ?? new WindowsRegistryReader();
        _fallbackPaths = fallbackPaths ?? DefaultFallbackPaths;
    }

    public SteamLocation DetectSteam()
    {
        var installPath = ResolveInstallPath();
        if (installPath is null)
            return new SteamLocation();

        var executablePath = Path.Combine(installPath, "steam.exe");
        return new SteamLocation { InstallPath = installPath, ExecutablePath = executablePath };
    }

    public IReadOnlyList<string> GetLibraryFolders(string steamInstallPath)
    {
        var vdfPath = Path.Combine(steamInstallPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
            return Array.Empty<string>();

        return ParseLibraryFolders(File.ReadAllText(vdfPath));
    }

    public GameLocation? LocateGame(string steamInstallPath)
    {
        if (string.IsNullOrWhiteSpace(steamInstallPath))
            return null;

        var libraries = new List<string> { steamInstallPath };
        libraries.AddRange(GetLibraryFolders(steamInstallPath));

        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(
                library, "steamapps", $"appmanifest_{DeadlockConstants.AppIdString}.acf");

            if (!File.Exists(manifestPath))
                continue;

            var installdir = ParseAppManifest(File.ReadAllText(manifestPath));
            if (string.IsNullOrWhiteSpace(installdir))
                continue;

            var installDirectory = Path.Combine(library, "steamapps", "common", installdir);
            var executablePath = FindGameExecutable(installDirectory);

            if (executablePath is not null)
                return CreateGameLocation(executablePath, library);
        }

        return null;
    }

    public GameLocation? ValidateGamePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (File.Exists(fullPath))
            return CreateGameLocation(fullPath);

        if (!Directory.Exists(fullPath))
            return null;

        var candidateDirectories = new[]
        {
            Path.Combine(fullPath, DeadlockConstants.GameExecutableRelativePath),
            Path.Combine(fullPath, "bin", "win64"),
            fullPath,
        };

        foreach (var directory in candidateDirectories)
        {
            var executablePath = FindGameExecutableInDirectory(directory);
            if (executablePath is not null)
                return CreateGameLocation(executablePath);
        }

        return null;
    }

    /// <summary>Parses libraryfolders.vdf text into a list of library paths.</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string vdfContent)
    {
        var root = VdfParser.Parse(vdfContent);
        var libraries = new List<string>();

        var folders = root.GetObject("libraryfolders") ?? root;

        if (folders.GetString("path") is { Length: > 0 } rootPath)
            libraries.Add(rootPath);

        foreach (var entry in folders.Entries)
        {
            if (entry.Value.IsObject && entry.Value.AsObject!.GetString("path") is { Length: > 0 } path)
                libraries.Add(path);
        }

        return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Parses appmanifest_*.acf text and returns the "installdir" value.</summary>
    public static string? ParseAppManifest(string acfContent)
    {
        var root = VdfParser.Parse(acfContent);
        var appState = root.GetObject("AppState") ?? root;
        return appState.GetString("installdir");
    }

    private static string? FindGameExecutable(string installDirectory)
    {
        return FindGameExecutableInDirectory(
            Path.Combine(installDirectory, DeadlockConstants.GameExecutableRelativePath));
    }

    private static string? FindGameExecutableInDirectory(string directory)
    {
        foreach (var executableName in KnownGameExecutables)
        {
            var executablePath = Path.Combine(directory, executableName);
            if (File.Exists(executablePath))
                return executablePath;
        }

        return null;
    }

    private static GameLocation? CreateGameLocation(string executablePath, string? libraryFolder = null)
    {
        var fileName = Path.GetFileName(executablePath);
        if (!KnownGameExecutables.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            return null;

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

        return new GameLocation
        {
            LibraryFolder = libraryFolder,
            InstallDirectory = installDirectory.FullName,
            ExecutablePath = Path.GetFullPath(executablePath),
        };
    }

    private string? ResolveInstallPath()
    {
        var registryCandidates = new[]
        {
            _registry.ReadCurrentUser(@"Software\Valve\Steam", "SteamPath"),
            _registry.ReadLocalMachine(@"SOFTWARE\Valve\Steam", "InstallPath"),
            _registry.ReadLocalMachine32(@"SOFTWARE\Valve\Steam", "InstallPath"),
        };

        return registryCandidates
            .Concat(_fallbackPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.TrimEnd('\\'))
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "steam.exe")));
    }
}
