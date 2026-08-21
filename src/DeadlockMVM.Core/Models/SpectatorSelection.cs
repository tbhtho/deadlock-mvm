namespace DeadlockMVM.Core.Models;

/// <summary>Spectator camera mode the client was last commanded into.</summary>
public enum SpecCameraMode
{
    Unknown,
    FreeRoam,
    InEye,
    Chase,
}

/// <summary>
/// The spectator target/mode as tracked from MVM-issued commands. This build of
/// Deadlock exposes no console query for the current spectator target or mode
/// (live-tested: spec_mode/spec_target/spec_player print nothing when queried),
/// so this state is locally tracked, not engine-authoritative. It drifts if the
/// user changes the target through the game itself.
/// </summary>
public sealed record SpectatorSelection
{
    public int? PlayerSlot { get; init; }

    public SpecCameraMode Mode { get; init; } = SpecCameraMode.Unknown;
}
