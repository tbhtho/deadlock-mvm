namespace DeadlockMVM.Core.Models;

public enum CampathPlayMode
{
    FromStart,
    FromCurrent,
}

public enum CampathEndBehavior : uint
{
    StopAndRelease = 0,
    HoldFinalCamera = 1,
    Loop = 2,
}

public enum CampathPlaybackState
{
    Idle,
    Validating,
    PreparingFreeRoam,
    SeekingToStart,
    WaitingForLandedTick,
    ReacquiringFreeRoam,
    TransferringPath,
    ArmingNativeCamera,
    WaitingForOwnership,
    Playing,
    Completed,
    Stopping,
    Stopped,
    Error,
    Cancelled,
}

public enum CampathStartFailure
{
    None,
    ReplayUnavailable,
    NativeBackendDisconnected,
    ProtocolMismatch,
    SignaturesUnavailable,
    ReplayHeartbeatStale,
    NotInFreeRoam,
    ObserverChainInvalid,
    CameraPointerInvalid,
    PathHasTooFewKeyframes,
    CurrentTickOutsidePath,
    SeekFailed,
    SeekTimedOut,
    FreeRoamReacquisitionFailed,
    CameraOwnershipRejected,
    PathTransferFailed,
    NativePathNotArmed,
    Cancelled,
    UnexpectedFailure,
}

public sealed record CampathPlaybackStatus(
    CampathPlaybackState State,
    CampathStartFailure Failure,
    string Detail,
    long? RequestedTick = null,
    long? ActualTick = null)
{
    public bool IsTerminal => State is CampathPlaybackState.Completed or CampathPlaybackState.Stopped or
        CampathPlaybackState.Error or CampathPlaybackState.Cancelled;
}

public sealed class CampathStartException : InvalidOperationException
{
    public CampathStartException(CampathStartFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        if (failure == CampathStartFailure.None)
            throw new ArgumentOutOfRangeException(nameof(failure), "A failed Campath start must have a typed reason.");
        Failure = failure;
    }

    public CampathStartFailure Failure { get; }
}

/// <summary>Small, testable transition guard for the Campath start/stop workflow.</summary>
public sealed class CampathPlaybackStateMachine
{
    private static readonly IReadOnlyDictionary<CampathPlaybackState, CampathPlaybackState[]> Allowed =
        new Dictionary<CampathPlaybackState, CampathPlaybackState[]>
        {
            [CampathPlaybackState.Idle] = [CampathPlaybackState.Validating, CampathPlaybackState.Stopping],
            [CampathPlaybackState.Validating] =
                [CampathPlaybackState.PreparingFreeRoam, CampathPlaybackState.Stopping],
            [CampathPlaybackState.PreparingFreeRoam] =
                [CampathPlaybackState.SeekingToStart, CampathPlaybackState.TransferringPath,
                 CampathPlaybackState.Stopping],
            [CampathPlaybackState.SeekingToStart] =
                [CampathPlaybackState.WaitingForLandedTick, CampathPlaybackState.Stopping],
            [CampathPlaybackState.WaitingForLandedTick] =
                [CampathPlaybackState.ReacquiringFreeRoam, CampathPlaybackState.Stopping],
            [CampathPlaybackState.ReacquiringFreeRoam] =
                [CampathPlaybackState.TransferringPath, CampathPlaybackState.Stopping],
            [CampathPlaybackState.TransferringPath] =
                [CampathPlaybackState.ArmingNativeCamera, CampathPlaybackState.Stopping],
            [CampathPlaybackState.ArmingNativeCamera] =
                [CampathPlaybackState.WaitingForOwnership, CampathPlaybackState.Stopping],
            [CampathPlaybackState.WaitingForOwnership] =
                [CampathPlaybackState.Playing, CampathPlaybackState.Stopping],
            [CampathPlaybackState.Playing] =
                [CampathPlaybackState.Completed, CampathPlaybackState.WaitingForLandedTick,
                 CampathPlaybackState.Stopping],
            [CampathPlaybackState.Completed] = [CampathPlaybackState.Stopping, CampathPlaybackState.Validating],
            [CampathPlaybackState.Stopping] = [CampathPlaybackState.Stopped],
            [CampathPlaybackState.Stopped] = [CampathPlaybackState.Validating, CampathPlaybackState.Stopping],
            [CampathPlaybackState.Error] = [CampathPlaybackState.Validating, CampathPlaybackState.Stopping],
            [CampathPlaybackState.Cancelled] = [CampathPlaybackState.Validating, CampathPlaybackState.Stopping],
        };

    public CampathPlaybackStatus Status { get; private set; } =
        new(CampathPlaybackState.Idle, CampathStartFailure.None, "Campath idle.");

    public CampathPlaybackStatus Transition(
        CampathPlaybackState next,
        string detail,
        long? requestedTick = null,
        long? actualTick = null)
    {
        if (next is CampathPlaybackState.Error or CampathPlaybackState.Cancelled)
            throw new ArgumentException("Use Fail or Cancel for terminal failure transitions.", nameof(next));
        if (!Allowed.TryGetValue(Status.State, out var allowed) || !allowed.Contains(next))
            throw new InvalidOperationException($"Invalid Campath transition {Status.State} -> {next}.");
        Status = new CampathPlaybackStatus(next, CampathStartFailure.None, detail, requestedTick, actualTick);
        return Status;
    }

    public CampathPlaybackStatus Fail(CampathStartFailure failure, string detail)
    {
        if (failure == CampathStartFailure.None)
            throw new ArgumentOutOfRangeException(nameof(failure), "A failed Campath start must have a typed reason.");
        Status = new CampathPlaybackStatus(CampathPlaybackState.Error, failure, detail);
        return Status;
    }

    public CampathPlaybackStatus Cancel(string detail)
    {
        Status = new CampathPlaybackStatus(CampathPlaybackState.Cancelled, CampathStartFailure.Cancelled, detail);
        return Status;
    }
}
