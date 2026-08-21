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

    /// <summary>Total demo ticks as reported by the engine.</summary>
    public int? TotalTicks { get; init; }

    /// <summary>
    /// Last playback speed commanded through MVM. The engine offers no query
    /// for demo_timescale, so this is a commanded value, not engine-reported.
    /// </summary>
    public double? Timescale { get; init; }

    /// <summary>Demo file name as reported by the engine (e.g. "replays/75438101-6448.dem").</summary>
    public string? ReplayName { get; init; }
}
