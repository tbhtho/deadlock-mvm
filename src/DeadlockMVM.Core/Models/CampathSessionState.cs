namespace DeadlockMVM.Core.Models;

/// <summary>
/// Campath workspace session state. The editor never silently restores a path:
/// a normal startup is <see cref="NoPath"/> until the user explicitly creates,
/// loads, or recovers a path, or adds the first keyframe (which creates a draft).
/// </summary>
public enum CampathSessionState : uint
{
    /// <summary>No loaded path and no keyframes; a clean workspace.</summary>
    NoPath = 0,

    /// <summary>Unsaved working path (autosaved only to the draft recovery file).</summary>
    DraftPath = 1,

    /// <summary>Explicitly named and saved path, associated with the active replay.</summary>
    SavedPath = 2,
}
