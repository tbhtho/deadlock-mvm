using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Services;

public readonly record struct ReplayPlaybackConfiguration(bool? IsPaused, double? Timescale);

public readonly record struct CampathCaptureAvailability(
    bool DeadlockRunning,
    bool ReplayActive,
    bool NativeBackendReady,
    bool CameraReadable,
    bool FreeRoam,
    bool CameraOwned,
    bool OperationInFlight);

public static class CampathCaptureGate
{
    public static bool CanCapture(CampathCaptureAvailability availability) =>
        availability.DeadlockRunning && availability.ReplayActive && availability.NativeBackendReady &&
        availability.CameraReadable && availability.FreeRoam && !availability.CameraOwned &&
        !availability.OperationInFlight;
}

/// <summary>
/// Passive capture boundary: obtains one authoritative native frame and verifies
/// that the operation did not change pause/resume or timescale configuration.
/// </summary>
public sealed class PassiveCampathCapture
{
    private readonly Func<CancellationToken, Task<CampathKeyframe>> _capture;
    private readonly Func<ReplayPlaybackConfiguration> _playback;

    public PassiveCampathCapture(
        Func<CancellationToken, Task<CampathKeyframe>> capture,
        Func<ReplayPlaybackConfiguration> playback)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public async Task<CampathKeyframe> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var before = _playback();
        var keyframe = await _capture(cancellationToken).ConfigureAwait(false);
        var after = _playback();
        if (before.IsPaused is not null && after.IsPaused != before.IsPaused)
            throw new InvalidOperationException("Passive keyframe capture changed replay pause state.");
        if (before.Timescale is not null && after.Timescale != before.Timescale)
            throw new InvalidOperationException("Passive keyframe capture changed replay timescale.");
        return keyframe;
    }
}

public static class CampathKeyframeEditor
{
    /// <returns>True when an existing keyframe at the same replay tick was replaced.</returns>
    public static bool Upsert(IList<CampathKeyframe> keyframes, CampathKeyframe keyframe)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        ArgumentNullException.ThrowIfNull(keyframe);
        if (!keyframe.IsValid)
            throw new ArgumentOutOfRangeException(nameof(keyframe));

        var replaced = false;
        for (var index = 0; index < keyframes.Count; index++)
        {
            if (keyframes[index].DemoTick == keyframe.DemoTick)
            {
                keyframes.RemoveAt(index);
                replaced = true;
                break;
            }
        }
        var ordered = keyframes.Append(keyframe).OrderBy(item => item.DemoTick).ToArray();
        keyframes.Clear();
        foreach (var item in ordered)
            keyframes.Add(item);
        return replaced;
    }
}
