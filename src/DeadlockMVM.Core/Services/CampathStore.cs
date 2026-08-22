using System.Text;
using System.Text.Json;
using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Services;

/// <summary>Validated, versioned persistence under the user's local application data.</summary>
public sealed class CampathStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;

    public CampathStore(string? directory = null) =>
        _directory = directory ?? AppPaths.CampathsDirectory;

    public string Save(CampathProject project, string? existingPath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(project);
        Directory.CreateDirectory(_directory);

        var path = existingPath;
        if (string.IsNullOrWhiteSpace(path))
            path = CreateAvailablePath(project.Name);
        path = Path.GetFullPath(path);
        if (!IsInsideDirectory(path))
            throw new InvalidOperationException("Campath files must remain in the MVM campaths directory.");

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(project, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, path, true);
        return path;
    }

    public CampathProject Load(
        string path,
        CampathReplayIdentifier? expectedReplay = null,
        bool allowReplayMismatch = false)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsInsideDirectory(fullPath))
            throw new InvalidDataException("Campath path is outside the MVM user-data directory.");

        CampathProject project;
        try
        {
            project = JsonSerializer.Deserialize<CampathProject>(File.ReadAllText(fullPath), JsonOptions)
                ?? throw new InvalidDataException("Campath file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Campath file contains invalid JSON.", ex);
        }

        Validate(project);
        if (expectedReplay is not null && !project.ReplayIdentifier.Matches(expectedReplay) && !allowReplayMismatch)
            throw new InvalidDataException(
                $"This Campath belongs to replay '{project.ReplayIdentifier.ReplayName}', not '{expectedReplay.ReplayName}'.");
        return project;
    }

    public IReadOnlyList<CampathDocumentInfo> List()
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<CampathDocumentInfo>();

        var results = new List<CampathDocumentInfo>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.campath.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var project = Load(path);
                results.Add(new CampathDocumentInfo(project.Name, path, project.ReplayIdentifier));
            }
            catch (InvalidDataException)
            {
                // A corrupt document stays on disk for recovery, but is not offered as loadable.
            }
        }
        return results.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string((name ?? string.Empty)
            .Trim()
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray()).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled Campath" : cleaned;
    }

    private string CreateAvailablePath(string name)
    {
        var stem = SanitizeName(name);
        var candidate = Path.Combine(_directory, $"{stem}.campath.json");
        for (var suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(_directory, $"{stem} ({suffix}).campath.json");
        return candidate;
    }

    private bool IsInsideDirectory(string path)
    {
        var root = Path.GetFullPath(_directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void Validate(CampathProject project)
    {
        if (project.SchemaVersion != CampathProject.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported Campath schema version {project.SchemaVersion}.");
        if (!project.IsValid)
            throw new InvalidDataException("Campath document is incomplete or contains invalid camera data.");
        if (project.Keyframes.GroupBy(keyframe => keyframe.DemoTick).Any(group => group.Count() > 1))
            throw new InvalidDataException("Campath document contains duplicate replay ticks.");
    }
}
