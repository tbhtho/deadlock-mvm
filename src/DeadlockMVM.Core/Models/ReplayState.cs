namespace DeadlockMVM.Core.Models;

/// <summary>
/// Snapshot of what is known about the replay currently playing in Deadlock.
/// Values the engine cannot report stay <c>null</c> — the UI must present
/// those as unavailable instead of guessing.
/// </summary>
public sealed record ReplayState
{
    public static readonly ReplayState Empty = new();

    /// <summary>True while the MVM command channel is connected to the game.</summary>
    public bool Connected { get; init; }

    /// <summary>Engine-reported pause state; null until the engine reveals it.</summary>
    public bool? IsPaused { get; init; }

    /// <summary>Current demo tick as reported by the engine.</summary>
    public int? CurrentTick { get; init; }

    /// <summary>
    /// True after Source reports that the demo host has activated. Position
    /// telemetry can appear while the map is still loading; camera ownership
    /// must not begin in that provisional window.
    /// </summary>
    public bool PlaybackHostActive { get; init; }

    /// <summary>Total demo ticks as reported by the engine.</summary>
    public int? TotalTicks { get; init; }

    /// <summary>
    /// Last known demo_timescale. The canonical requested value is surfaced
    /// immediately and replaced by engine readback when the console reports it.
    /// </summary>
    public double? Timescale { get; init; }

    /// <summary>Demo file name as reported by the engine (e.g. "replays/75438101-6448.dem").</summary>
    public string? ReplayName { get; init; }

    /// <summary>
    /// Monotonic controller-local demo-load generation. Unlike file name and
    /// total ticks, this advances when Source reports a real same-file reload.
    /// It survives transient VConsole reconnects within that replay session.
    /// </summary>
    public long ReplaySessionGeneration { get; init; }
}
