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

    /// <summary>Recovery location for the unsaved draft; never listed as a saved path.</summary>
    public string DraftFilePath => Path.Combine(_directory, "draft.campath.json");

    /// <summary>
    /// Session sentinel for the draft. It exists only while a session owns the
    /// draft: a clean application exit removes it, so a lock still present at
    /// startup means the previous session ended abnormally.
    /// </summary>
    public string DraftLockFilePath => Path.Combine(_directory, "draft.lock");

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
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(project, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            // A failed write or move must not leave a stray .tmp beside the real
            // document, where it is never listed and never cleaned up.
            TryDelete(temporary);
        }
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
            if (string.Equals(path, DraftFilePath, StringComparison.OrdinalIgnoreCase))
                continue; // The recovery draft is never offered as a normal saved path.
            try
            {
                var project = Load(path);
                results.Add(new CampathDocumentInfo(
                    project.Name,
                    path,
                    project.ReplayIdentifier,
                    project.Keyframes.Count,
                    File.GetLastWriteTimeUtc(path)));
            }
            catch (InvalidDataException)
            {
                // A corrupt document stays on disk for recovery, but is not offered as loadable.
            }
        }
        return results.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Persists the unsaved working draft and marks this session as its owner.</summary>
    public void SaveDraft(CampathProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(project);
        Directory.CreateDirectory(_directory);
        var temporary = DraftFilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(project, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, DraftFilePath, true);
        MarkDraftActive();
    }

    /// <summary>Records that the current (live) session owns the on-disk draft.</summary>
    public void MarkDraftActive()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            DraftLockFilePath,
            $"{Environment.ProcessId} {DateTime.UtcNow:O}",
            new UTF8Encoding(false));
    }

    /// <summary>Removes the draft and its sentinel (draft saved, closed, or discarded).</summary>
    public void DeleteDraft()
    {
        TryDelete(DraftLockFilePath);
        TryDelete(DraftFilePath);
    }

    /// <summary>
    /// Clean application shutdown: keeps the draft for explicit recovery but removes
    /// the sentinel so the next start does not treat it as a crash recovery.
    /// </summary>
    public void CompleteCleanShutdown() => TryDelete(DraftLockFilePath);

    /// <summary>True when a draft exists from a session that ended abnormally.</summary>
    public bool HasAbandonedDraft =>
        File.Exists(DraftFilePath) && File.Exists(DraftLockFilePath);

    /// <summary>
    /// Loads the draft when it exists and belongs to <paramref name="expectedReplay"/>;
    /// returns null when there is no usable draft. Never throws for a missing draft.
    /// </summary>
    public CampathProject? TryLoadDraft(CampathReplayIdentifier expectedReplay)
    {
        ArgumentNullException.ThrowIfNull(expectedReplay);
        if (!File.Exists(DraftFilePath))
            return null;
        try
        {
            return Load(DraftFilePath, expectedReplay);
        }
        catch (InvalidDataException)
        {
            return null; // Corrupt or foreign-replay draft: leave it on disk, do not offer it.
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A lingering sentinel only causes one extra recovery prompt; never fail closed here.
        }
        catch (UnauthorizedAccessException)
        {
        }
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
