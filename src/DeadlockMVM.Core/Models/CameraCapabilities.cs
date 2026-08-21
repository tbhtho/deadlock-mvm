namespace DeadlockMVM.Core.Models;

/// <summary>Describes which camera operations the current Deadlock build exposes.</summary>
public sealed record CameraCapabilities
{
    public bool CanReadTransform { get; init; }

    public bool CanWritePosition { get; init; }

    public bool CanWriteRotation { get; init; }

    public bool CanSelectPlayer { get; init; }

    public bool CanReadBaseFov { get; init; }

    public bool CanWriteBaseFov { get; init; }

    public bool CanReadActiveFov { get; init; }

    public bool CanWriteActiveFov { get; init; }

    public bool CanSaveRestore { get; init; }

    public string Limitation { get; init; } = string.Empty;
}
