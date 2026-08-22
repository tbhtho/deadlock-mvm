using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Contracts;

/// <summary>
/// Read-only view of replay playback state, used to gate engine-level writes to
/// confirmed demo playback. Implemented by <see cref="Services.ReplayController"/>.
/// </summary>
public interface IReplayPlaybackState
{
    ReplayState State { get; }

    event EventHandler<ReplayState>? StateChanged;
}
