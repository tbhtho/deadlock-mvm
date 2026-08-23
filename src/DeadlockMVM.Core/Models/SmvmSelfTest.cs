namespace DeadlockMVM.Core.Models;

public enum SmvmSelfTestKind
{
    Camera,
    Campath,
}

public enum SmvmSelfTestStage
{
    Idle,
    Validating,
    CapturingBaseline,
    ReleasingPriorOwnership,
    ApplyingProbe,
    TransferringPath,
    AcquiringOwnership,
    VerifyingAuthoritativeFrame,
    RestoringCamera,
    RestoringOwnership,
    Completed,
    Failed,
    Cancelled,
}

public enum SmvmSelfTestFailure
{
    None,
    NativeBackendDisconnected,
    ReplayUnavailable,
    NotInFreeRoam,
    CameraReadbackUnavailable,
    CameraAlreadyOwned,
    InvalidPath,
    CurrentTickOutsidePath,
    ProbeTransferRejected,
    PathTransferRejected,
    NativeOwnershipRejected,
    AuthoritativeMismatch,
    RestorationFailed,
    Cancelled,
    UnexpectedFailure,
}

public sealed record SmvmSelfTestResult(
    SmvmSelfTestKind Kind,
    SmvmSelfTestStage Stage,
    SmvmSelfTestFailure Failure,
    string Detail,
    CameraSample? Expected = null,
    CameraSample? Actual = null)
{
    public bool Succeeded => Stage == SmvmSelfTestStage.Completed && Failure == SmvmSelfTestFailure.None;
    public bool IsRunning => Stage is not SmvmSelfTestStage.Idle and not SmvmSelfTestStage.Completed and
        not SmvmSelfTestStage.Failed and not SmvmSelfTestStage.Cancelled;

    public static SmvmSelfTestResult Idle(SmvmSelfTestKind kind) =>
        new(kind, SmvmSelfTestStage.Idle, SmvmSelfTestFailure.None, $"{kind} self-test idle.");
}

public readonly record struct SmvmSelfTestRestorationPolicy(
    bool RestoreManualCamera,
    bool RestoreRollOverride,
    CameraSample Baseline);

public static class SmvmSelfTestPolicy
{
    public static SmvmSelfTestFailure ValidateCameraPrerequisites(
        bool connected,
        bool replayGate,
        bool freeRoam,
        bool cameraObserved,
        bool fullCameraOwned)
    {
        if (!connected) return SmvmSelfTestFailure.NativeBackendDisconnected;
        if (!replayGate) return SmvmSelfTestFailure.ReplayUnavailable;
        if (!freeRoam) return SmvmSelfTestFailure.NotInFreeRoam;
        if (fullCameraOwned) return SmvmSelfTestFailure.CameraAlreadyOwned;
        if (!cameraObserved) return SmvmSelfTestFailure.CameraReadbackUnavailable;
        return SmvmSelfTestFailure.None;
    }

    public static SmvmSelfTestFailure ValidateCampath(CampathPath? path, long currentTick)
    {
        if (path?.IsValid != true)
            return SmvmSelfTestFailure.InvalidPath;
        return currentTick < path.Keyframes[0].DemoTick || currentTick > path.Keyframes[^1].DemoTick
            ? SmvmSelfTestFailure.CurrentTickOutsidePath
            : SmvmSelfTestFailure.None;
    }

    public static CameraSample CreateCameraProbe(CameraSample baseline)
    {
        if (!baseline.IsValid)
            throw new ArgumentOutOfRangeException(nameof(baseline));
        var fov = baseline.Fov <= CameraSample.MaxFov - 0.75
            ? baseline.Fov + 0.75
            : baseline.Fov - 0.75;
        var roll = ManualCameraMath.NormalizeAngle(baseline.Roll + 3.0);
        return baseline with { Fov = fov, Roll = roll };
    }

    public static bool SamplesMatch(CameraSample expected, CameraSample actual, double tolerance = 0.025)
    {
        if (!expected.IsValid || !actual.IsValid || !double.IsFinite(tolerance) || tolerance < 0)
            return false;
        return Math.Abs(expected.X - actual.X) <= tolerance &&
               Math.Abs(expected.Y - actual.Y) <= tolerance &&
               Math.Abs(expected.Z - actual.Z) <= tolerance &&
               Math.Abs(ShortestAngleDelta(expected.Pitch, actual.Pitch)) <= tolerance &&
               Math.Abs(ShortestAngleDelta(expected.Yaw, actual.Yaw)) <= tolerance &&
               Math.Abs(ShortestAngleDelta(expected.Roll, actual.Roll)) <= tolerance &&
               Math.Abs(expected.Fov - actual.Fov) <= tolerance;
    }

    public static SmvmSelfTestRestorationPolicy CreateRestorationPolicy(
        CameraSample baseline,
        bool manualCameraDesired,
        bool rollOverrideActive)
    {
        if (!baseline.IsValid)
            throw new ArgumentOutOfRangeException(nameof(baseline));
        return new SmvmSelfTestRestorationPolicy(manualCameraDesired, rollOverrideActive, baseline);
    }

    private static double ShortestAngleDelta(double from, double to)
    {
        var delta = (to - from) % 360.0;
        if (delta > 180.0) delta -= 360.0;
        if (delta < -180.0) delta += 360.0;
        return delta;
    }
}
