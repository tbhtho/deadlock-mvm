namespace DeadlockMVM.Core.Models;

/// <summary>Describes which camera operations the current Deadlock build exposes.</summary>
public sealed record CameraCapabilities
{
    public bool CanReadActiveTransform { get; init; }

    public bool CanMoveRoamTarget { get; init; }

    public bool CanSelectPlayer { get; init; }

    public bool CanReadBaseFov { get; init; }

    public bool CanWriteBaseFov { get; init; }

    public bool CanWriteActiveRotation { get; init; }

    public bool CanReadActiveFov { get; init; }

    public bool CanSaveRestoreDeterministically { get; init; }

    public string Limitation { get; init; } = string.Empty;
}
