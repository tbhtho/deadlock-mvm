namespace DeadlockMVM.Core.Native.InProcess;

internal readonly record struct ReplayProcessIdentity(int ProcessId, long StartTimeUtcFileTime);

internal enum PendingRemoteLoadDisposition
{
    StartNew,
    PollExisting,
    RetireStale,
}

internal enum RemoteThreadPollObservation
{
    Completed,
    StillRunning,
    Indeterminate,
}

internal static class PendingRemoteLoadPolicy
{
    internal static PendingRemoteLoadDisposition Reconcile(
        ReplayProcessIdentity current,
        ReplayProcessIdentity? pending)
    {
        if (pending is null)
            return PendingRemoteLoadDisposition.StartNew;

        return pending.Value == current
            ? PendingRemoteLoadDisposition.PollExisting
            : PendingRemoteLoadDisposition.RetireStale;
    }

    internal static bool MustRetainResources(RemoteThreadPollObservation observation) =>
        observation is RemoteThreadPollObservation.StillRunning or
                       RemoteThreadPollObservation.Indeterminate;
}
