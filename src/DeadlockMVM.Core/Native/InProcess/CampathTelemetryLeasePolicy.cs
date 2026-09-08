using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;

namespace DeadlockMVM.Core.Native.InProcess;

/// <summary>
/// Keeps an already-validated native Campath authoritative while VConsole is
/// temporarily unavailable during an offline movie capture. The native replay
/// clock and command-line replay gate remain the physical safety boundary.
/// </summary>
public static class CampathTelemetryLeasePolicy
{
    public static CampathHeartbeatTelemetry ResolveHeartbeat(
        ReplayState replay,
        int? currentGameTickOffset,
        SpecCameraMode currentCameraMode,
        bool campathPlaying,
        CampathPlaybackState playbackState,
        long campathReplaySessionGeneration,
        int campathGameTickOffset,
        InProcessCameraStatus? nativeStatus)
    {
        var managedReplayActive = IsConfirmedReplay(replay);
        if (!campathPlaying || playbackState != CampathPlaybackState.Playing)
        {
            return new CampathHeartbeatTelemetry(
                managedReplayActive,
                currentCameraMode == SpecCameraMode.FreeRoam,
                replay.CurrentTick ?? -1,
                currentGameTickOffset ?? -1,
                ProtectingCampath: false);
        }

        var replayAuthoritativelyEnded = replay.Connected &&
                                          !string.IsNullOrWhiteSpace(replay.ReplayName) &&
                                          replay.CurrentTick is { } currentTick &&
                                          replay.TotalTicks is { } totalTicks &&
                                          currentTick >= totalTicks;
        if (replayAuthoritativelyEnded)
        {
            return new CampathHeartbeatTelemetry(
                ReplayActive: false,
                FreeRoam: false,
                ReplayTick: -1,
                GameTickOffset: -1,
                ProtectingCampath: false);
        }

        // A same-file reload increments ReplaySessionGeneration. Never carry an
        // old path across that boundary, even when its name happens to match.
        var sameReplaySession = campathReplaySessionGeneration > 0 &&
                                replay.ReplaySessionGeneration == campathReplaySessionGeneration;
        var nativeReplayStillValid = nativeStatus is not null &&
                                     nativeStatus.Flags.HasFlag(InProcessStatusFlags.CommandLineReplay);
        var replayTick = replay.CurrentTick ?? nativeStatus?.ReplayTick ?? -1;
        var gameTickOffset = currentGameTickOffset ?? campathGameTickOffset;
        if (!sameReplaySession || !nativeReplayStillValid || replayTick < 0 || gameTickOffset < 0)
        {
            return new CampathHeartbeatTelemetry(
                ReplayActive: false,
                FreeRoam: false,
                ReplayTick: -1,
                GameTickOffset: -1,
                ProtectingCampath: false);
        }

        // The hook independently validates the live observer camera and derives
        // replay time from engine globals. Preserve the last calibrated offset
        // instead of overwriting it with -1 during a VConsole reconnect.
        return new CampathHeartbeatTelemetry(
            ReplayActive: true,
            FreeRoam: true,
            ReplayTick: replayTick,
            GameTickOffset: gameTickOffset,
            ProtectingCampath: !managedReplayActive || currentGameTickOffset is null);
    }

    public static bool KeepsCameraTransactionActive(
        bool managedReplayActive,
        bool campathPlaying,
        bool nativeCampathActive) =>
        managedReplayActive || campathPlaying || nativeCampathActive;

    public static bool ShouldIgnoreTerminalDuringCompositing(
        CampathPlaybackStatus status,
        MovieCompositingStage stage,
        bool anotherCampathIsPlaying) =>
        status.State == CampathPlaybackState.Completed &&
        stage != MovieCompositingStage.None &&
        anotherCampathIsPlaying;

    private static bool IsConfirmedReplay(ReplayState replay) =>
        replay.Connected &&
        !string.IsNullOrWhiteSpace(replay.ReplayName) &&
        replay.CurrentTick is not null &&
        (replay.TotalTicks is null || replay.CurrentTick < replay.TotalTicks);
}

public readonly record struct CampathHeartbeatTelemetry(
    bool ReplayActive,
    bool FreeRoam,
    long ReplayTick,
    long GameTickOffset,
    bool ProtectingCampath);
