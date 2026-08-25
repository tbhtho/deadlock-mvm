namespace DeadlockMVM.Core.Services;

/// <summary>
/// Arbitrates the once-per-replay 100% speed baseline against later explicit
/// owner choices. Captured startup leases become stale at owner input and at a
/// Deadlock process boundary, so an awaited startup pass cannot overwrite a
/// newer custom speed.
/// </summary>
public sealed class DemoPlaybackSpeedPolicy
{
    private readonly object _gate = new();
    private long _ownerIntentEpoch;
    private string _normalizedReplayIdentity = string.Empty;
    private long _normalizedByOwnerIntentEpoch;
    private bool _ownerIntentPendingForNextReplay;

    public DemoPlaybackSpeedLease CaptureLease()
    {
        lock (_gate)
            return new DemoPlaybackSpeedLease(_ownerIntentEpoch);
    }

    public void ResetForNewProcess()
    {
        lock (_gate)
        {
            _ownerIntentEpoch++;
            _normalizedReplayIdentity = string.Empty;
            _normalizedByOwnerIntentEpoch = 0;
            _ownerIntentPendingForNextReplay = false;
        }
    }

    public long MarkOwnerIntent(string? replayIdentity)
    {
        lock (_gate)
        {
            _ownerIntentEpoch++;
            if (string.IsNullOrWhiteSpace(replayIdentity))
            {
                _ownerIntentPendingForNextReplay = true;
                return _ownerIntentEpoch;
            }

            _normalizedReplayIdentity = replayIdentity.Trim();
            _normalizedByOwnerIntentEpoch = _ownerIntentEpoch;
            _ownerIntentPendingForNextReplay = false;
            return _ownerIntentEpoch;
        }
    }

    public bool TryClaimStartupNormalization(
        string replayIdentity,
        DemoPlaybackSpeedLease lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayIdentity);
        var normalizedIdentity = replayIdentity.Trim();
        lock (_gate)
        {
            if (_ownerIntentPendingForNextReplay)
            {
                _normalizedReplayIdentity = normalizedIdentity;
                _normalizedByOwnerIntentEpoch = _ownerIntentEpoch;
                _ownerIntentPendingForNextReplay = false;
                return false;
            }
            if (lease.OwnerIntentEpoch != _ownerIntentEpoch ||
                string.Equals(
                    _normalizedReplayIdentity,
                    normalizedIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }

            _normalizedReplayIdentity = normalizedIdentity;
            _normalizedByOwnerIntentEpoch = 0;
            return true;
        }
    }

    public void MarkNormalizationFailed(
        string replayIdentity,
        DemoPlaybackSpeedLease lease)
    {
        lock (_gate)
        {
            if (lease.OwnerIntentEpoch == _ownerIntentEpoch &&
                _normalizedByOwnerIntentEpoch == 0 &&
                string.Equals(
                    _normalizedReplayIdentity,
                    replayIdentity.Trim(),
                    StringComparison.Ordinal))
            {
                _normalizedReplayIdentity = string.Empty;
            }
        }
    }

    public void ClearForNoReplay()
    {
        lock (_gate)
        {
            _normalizedReplayIdentity = string.Empty;
            _normalizedByOwnerIntentEpoch = 0;
            _ownerIntentPendingForNextReplay = false;
        }
    }

    public bool IsOwnerActionCurrent(
        DemoPlaybackSpeedActionLease lease,
        long currentProcessBoundaryEpoch,
        long currentNativeConnectionEpoch,
        long currentPresentationReplayEpoch,
        string? currentReplayIdentity,
        bool nativeConnected)
    {
        lock (_gate)
        {
            if (lease.OwnerIntentEpoch != _ownerIntentEpoch ||
                !nativeConnected ||
                lease.ProcessBoundaryEpoch != currentProcessBoundaryEpoch ||
                lease.NativeConnectionEpoch != currentNativeConnectionEpoch)
            {
                return false;
            }

            // An action issued from authoritative replay A is scoped to A. A
            // provisional loading action can target only the current epoch or
            // its immediately following authoritative replay.
            return lease.ReplayIdentity is null
                ? currentPresentationReplayEpoch == lease.PresentationReplayEpoch ||
                  currentPresentationReplayEpoch == lease.PresentationReplayEpoch + 1
                : (lease.PresentationReplayEpoch == currentPresentationReplayEpoch ||
                   lease.PresentationReplayEpoch + 1 == currentPresentationReplayEpoch) &&
                  string.Equals(
                      lease.ReplayIdentity,
                      currentReplayIdentity,
                      StringComparison.Ordinal);
        }
    }

    public bool ReleaseFailedOwnerIntent(DemoPlaybackSpeedActionLease lease)
    {
        lock (_gate)
        {
            if (lease.OwnerIntentEpoch != _ownerIntentEpoch)
                return false;

            if (_normalizedByOwnerIntentEpoch == lease.OwnerIntentEpoch)
            {
                _normalizedReplayIdentity = string.Empty;
                _normalizedByOwnerIntentEpoch = 0;
            }
            _ownerIntentPendingForNextReplay = false;
            _ownerIntentEpoch++;
            return true;
        }
    }
}

public readonly record struct DemoPlaybackSpeedLease(long OwnerIntentEpoch);

public readonly record struct DemoPlaybackSpeedActionLease(
    long OwnerIntentEpoch,
    long ProcessBoundaryEpoch,
    long NativeConnectionEpoch,
    long PresentationReplayEpoch,
    string? ReplayIdentity);
