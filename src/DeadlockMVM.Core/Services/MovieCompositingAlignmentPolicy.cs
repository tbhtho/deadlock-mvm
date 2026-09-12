namespace DeadlockMVM.Core.Services;

public readonly record struct MoviePassAlignmentMetrics(
    long? Frames,
    long? FirstReplayTick,
    long? LastReplayTick,
    ulong? ReplayTimingDigest,
    long? RenderedCameraSamples,
    ulong? RenderedCameraDigest);

public readonly record struct MovieGreenscreenPlateMetrics(
    long? Frames,
    long? BackgroundPixels,
    long? SubjectPixels,
    long? FramesWithBackground,
    long? FramesWithSubject,
    long? RepeatedVisualSamples = null);

public static class MovieCompositingAlignmentPolicy
{
    public static bool IsGreenscreenPlateUsable(MovieGreenscreenPlateMetrics plate) =>
        plate.Frames is > 0 &&
        plate.BackgroundPixels is > 0 &&
        plate.SubjectPixels is > 0 &&
        plate.FramesWithBackground == plate.Frames &&
        plate.FramesWithSubject is > 0 &&
        plate.FramesWithSubject <= plate.Frames &&
        // A plate whose sampled frames barely change is a still image, not a
        // key layer. Unknown legacy metrics stay permitted so old reports can
        // still be inspected; new writes always carry the counter.
        (plate.RepeatedVisualSamples is not { } repeated ||
         plate.Frames is not { } frames ||
         repeated * 2 < frames);

    public static bool IsFrameTickAndTimingAligned(
        MoviePassAlignmentMetrics world,
        MoviePassAlignmentMetrics chroma) =>
        world.Frames is > 0 &&
        world.Frames == chroma.Frames &&
        world.FirstReplayTick is >= 0 &&
        world.FirstReplayTick == chroma.FirstReplayTick &&
        world.LastReplayTick is >= 0 &&
        world.LastReplayTick == chroma.LastReplayTick &&
        world.LastReplayTick >= world.FirstReplayTick &&
        world.ReplayTimingDigest is > 0 &&
        world.ReplayTimingDigest == chroma.ReplayTimingDigest &&
        world.RenderedCameraSamples == world.Frames &&
        chroma.RenderedCameraSamples == chroma.Frames &&
        world.RenderedCameraDigest is > 0 &&
        world.RenderedCameraDigest == chroma.RenderedCameraDigest;
}
