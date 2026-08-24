namespace DeadlockMVM.Core.Models;

public sealed record CampathReplayIdentifier(string ReplayName, long? TotalTicks)
{
    public bool Matches(CampathReplayIdentifier other) =>
        string.Equals(Normalize(ReplayName), Normalize(other.ReplayName), StringComparison.OrdinalIgnoreCase) &&
        (TotalTicks is null || other.TotalTicks is null || TotalTicks == other.TotalTicks);

    private static string Normalize(string value) =>
        Path.GetFileNameWithoutExtension(value?.Replace('\\', '/') ?? string.Empty);
}

/// <summary>Versioned user-data document for a replay-associated cinematic path.</summary>
public sealed class CampathProject
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Name { get; init; } = "Untitled Campath";
    public CampathReplayIdentifier ReplayIdentifier { get; init; } = new(string.Empty, null);
    public CampathInterpolationMode InterpolationMode { get; init; } = CampathInterpolationMode.Linear;
    public CampathEasingMode EasingMode { get; init; } = CampathEasingMode.Linear;
    public CampathEndBehavior EndBehavior { get; init; } = CampathEndBehavior.StopAndRelease;
    public List<CampathKeyframe> Keyframes { get; init; } = [];

    public bool IsValid =>
        SchemaVersion == CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(Name) &&
        ReplayIdentifier is not null && !string.IsNullOrWhiteSpace(ReplayIdentifier.ReplayName) &&
        Keyframes is not null &&
        Keyframes.Count <= CampathPath.MaxKeyframes &&
        Keyframes.All(keyframe => keyframe.IsValid) &&
        Enum.IsDefined(InterpolationMode) && Enum.IsDefined(EasingMode) &&
        EndBehavior is CampathEndBehavior.StopAndRelease or CampathEndBehavior.HoldFinalCamera;

    public CampathPath ToPath() => new(Keyframes, InterpolationMode, EasingMode);
}

public sealed record CampathDocumentInfo(
    string Name,
    string FilePath,
    CampathReplayIdentifier ReplayIdentifier,
    int KeyframeCount = 0,
    DateTime? ModifiedUtc = null,
    bool IsDraft = false);
