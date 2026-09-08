using DeadlockMVM.Core.Native.InProcess;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Prevents a native overlay action from crossing into a replacement pipe,
/// Deadlock process, or replay after waiting in the managed action queue.
/// </summary>
public static class SmvmQueuedActionLeasePolicy
{
    public static bool IsCurrent(
        SmvmAction action,
        SmvmQueuedActionLease lease,
        long currentProcessBoundaryEpoch,
        long currentNativeConnectionEpoch,
        long currentPresentationReplayEpoch,
        long currentReplayConnectionGeneration,
        long currentReplaySessionGeneration,
        string? currentReplayName,
        bool nativeConnected,
        bool replayTelemetryAuthoritative)
    {
        var scope = GetScope(action);
        if (lease.ProcessBoundaryEpoch != currentProcessBoundaryEpoch)
        {
            return false;
        }
        if (scope != SmvmQueuedActionScope.PlaybackSpeed &&
            (!nativeConnected ||
             lease.NativeConnectionEpoch <= 0 ||
             lease.NativeConnectionEpoch != currentNativeConnectionEpoch))
        {
            return false;
        }

        return scope switch
        {
            SmvmQueuedActionScope.ProcessAndNative => true,
            // DemoPlaybackSpeedPolicy owns the current-or-immediate-next replay
            // rule. Speed is VConsole-only, so this layer fences the Deadlock
            // process but deliberately ignores the optional camera pipe.
            SmvmQueuedActionScope.PlaybackSpeed => true,
            // Escape/F9 are valid while replay telemetry is provisional, but
            // their delayed cleanup must never tear down the next replay.
            SmvmQueuedActionScope.PresentationReplay =>
                lease.PresentationReplayEpoch == currentPresentationReplayEpoch &&
                (lease.SourceReplaySessionGeneration > 0
                    ? (!replayTelemetryAuthoritative
                        ? lease.SourceReplaySessionGeneration == currentReplaySessionGeneration
                        : lease.SourceReplaySessionGeneration == lease.ReplaySessionGeneration &&
                      lease.ReplayConnectionGeneration == currentReplayConnectionGeneration &&
                      lease.ReplaySessionGeneration == currentReplaySessionGeneration &&
                      !string.IsNullOrWhiteSpace(lease.ReplayName) &&
                      string.Equals(
                          lease.ReplayName,
                          currentReplayName?.Trim(),
                          StringComparison.OrdinalIgnoreCase))
                    : !replayTelemetryAuthoritative && currentReplaySessionGeneration == 0),
            SmvmQueuedActionScope.ReplayIdentity =>
                replayTelemetryAuthoritative &&
                lease.SourceReplaySessionGeneration > 0 &&
                lease.SourceReplaySessionGeneration == lease.ReplaySessionGeneration &&
                (lease.PresentationReplayEpoch == currentPresentationReplayEpoch ||
                 lease.PresentationReplayEpoch + 1 == currentPresentationReplayEpoch) &&
                lease.ReplayConnectionGeneration == currentReplayConnectionGeneration &&
                lease.ReplaySessionGeneration == currentReplaySessionGeneration &&
                !string.IsNullOrWhiteSpace(lease.ReplayName) &&
                string.Equals(
                    lease.ReplayName,
                    currentReplayName?.Trim(),
                    StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public static SmvmQueuedActionScope GetScope(SmvmAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Type == SmvmActionType.SetTimescale)
            return SmvmQueuedActionScope.PlaybackSpeed;
        if (action.Type is SmvmActionType.RestoreDeadlockUi or
            SmvmActionType.ToggleManualCamera or
            SmvmActionType.StopCampath or
            SmvmActionType.StopMovieRecording ||
            action.Type == SmvmActionType.SetDeadlockUiMode &&
            action.Index == (int)DeadlockUiMode.DeadlockUi)
        {
            return SmvmQueuedActionScope.PresentationReplay;
        }

        return RequiresReplayIdentity(action.Type)
            ? SmvmQueuedActionScope.ReplayIdentity
            : SmvmQueuedActionScope.ProcessAndNative;
    }

    public static bool RequiresReplayLease(SmvmAction action) =>
        GetScope(action) == SmvmQueuedActionScope.ReplayIdentity;

    /// <summary>
    /// Playback speed owns only the engine clock and has its own process/replay
    /// fencing. It must not wait behind serialized seek or camera operations.
    /// </summary>
    public static bool BypassesSerializedCameraActionGate(SmvmAction action) =>
        GetScope(action) == SmvmQueuedActionScope.PlaybackSpeed;

    private static bool RequiresReplayIdentity(SmvmActionType action) => action is
        SmvmActionType.ToggleReplayPause or
        SmvmActionType.SetTimescale or
        SmvmActionType.SeekTick or
        SmvmActionType.StepBack or
        SmvmActionType.StepForward or
        SmvmActionType.FreeRoam or
        SmvmActionType.PreviousPlayer or
        SmvmActionType.NextPlayer or
        SmvmActionType.InEye or
        SmvmActionType.Chase or
        SmvmActionType.SetFov or
        SmvmActionType.SetRoll or
        SmvmActionType.SaveCamera or
        SmvmActionType.RestoreCamera or
        SmvmActionType.AddKeyframe or
        SmvmActionType.DeleteKeyframe or
        SmvmActionType.SelectKeyframe or
        SmvmActionType.GoToKeyframe or
        SmvmActionType.UpdateKeyframe or
        SmvmActionType.ClearPath or
        SmvmActionType.SetInterpolation or
        SmvmActionType.SetEasing or
        SmvmActionType.PlayFromStart or
        SmvmActionType.PlayFromCurrent or
        SmvmActionType.SetEndBehavior or
        SmvmActionType.UndoEdit or
        SmvmActionType.RedoEdit or
        SmvmActionType.SetPathName or
        SmvmActionType.SavePath or
        SmvmActionType.LoadNextPath or
        SmvmActionType.ReacquireCamera or
        SmvmActionType.CameraSelfTest or
        SmvmActionType.CampathSelfTest or
        SmvmActionType.NewPath or
        SmvmActionType.SavePathAs or
        SmvmActionType.LoadPath or
        SmvmActionType.ClosePath or
        SmvmActionType.RecoverDraft or
        SmvmActionType.DiscardDraft or
        SmvmActionType.RequestPathList or
        SmvmActionType.CaptureDiagnostic or
        SmvmActionType.SetMovieRecordingOption or
        SmvmActionType.SetMovieRecordingFps or
        SmvmActionType.SetMovieRecordingPreset or
        SmvmActionType.SetMovieOutputMode or
        SmvmActionType.SetMovieCapturePass or
        SmvmActionType.SetMovieOutputResolution or
        SmvmActionType.StartMovieRecording or
        SmvmActionType.SetRuleOfThirds or
        SmvmActionType.SetCustomFogEnabled or
        SmvmActionType.SetCustomFogValue or
        SmvmActionType.SetCustomFogColor or
        SmvmActionType.ResetCustomFog or
        SmvmActionType.SetGreenscreenMode or
        SmvmActionType.SetDeadlockUiMode or
        SmvmActionType.CycleReplayInterface;
}

public enum SmvmQueuedActionScope
{
    ProcessAndNative = 0,
    PresentationReplay = 1,
    ReplayIdentity = 2,
    PlaybackSpeed = 3,
}

public readonly record struct SmvmQueuedActionLease(
    long ProcessBoundaryEpoch,
    long NativeConnectionEpoch,
    long PresentationReplayEpoch,
    long ReplayConnectionGeneration = 0,
    long ReplaySessionGeneration = 0,
    string ReplayName = "",
    long SourceReplaySessionGeneration = 0);
