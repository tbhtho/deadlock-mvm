namespace DeadlockMVM.Core.Models;

/// <summary>Authoritative reason that manual camera control is or is not available.</summary>
public enum CameraAvailability
{
    Initializing,
    Ready,
    ReplayUnavailable,
    ReplaySeeking,
    NotInFreeRoam,
    ObserverTargetActive,
    CameraManagerUnavailable,
    CameraObjectUnavailable,
    CameraReadbackUnavailable,
    NativeBackendDisconnected,
    SignatureUnavailable,
    ManagedHostDisconnected,
    OwnershipHeldByCampath,
    OwnershipRejected,
    SnapshotStale,
    ProtocolMismatch,
}

/// <summary>Current owner of the renderer-facing camera.</summary>
public enum CameraOwnership
{
    None,
    DeadlockSpectator,
    SmvmManualCamera,
    SmvmRestore,
    SmvmCampath,
}

/// <summary>Pure transformations used by manual camera input.</summary>
public static class ManualCameraMath
{
    public const double MinPitch = -89;
    public const double MaxPitch = 89;

    /// <summary>Wraps an angle to the inclusive range [-180, 180].</summary>
    public static double NormalizeAngle(double angle)
    {
        ValidateFinite(angle, nameof(angle));

        angle %= 360;
        if (angle > 180) angle -= 360;
        if (angle < -180) angle += 360;
        return angle;
    }

    /// <summary>Clamps a finite pitch to the range supported by <see cref="CameraSample"/>.</summary>
    public static double ClampPitch(double pitch)
    {
        ValidateFinite(pitch, nameof(pitch));
        return Math.Clamp(pitch, MinPitch, MaxPitch);
    }

    /// <summary>
    /// Moves a camera along its local Source-engine axes. At zero rotation,
    /// forward is +X, right is -Y, and up is +Z. Orientation and FOV are unchanged.
    /// </summary>
    public static CameraSample MoveLocal(
        CameraSample sample,
        double forward,
        double right,
        double up,
        double distance)
    {
        if (!sample.IsValid)
            throw new ArgumentOutOfRangeException(nameof(sample), "The camera sample must be valid.");

        ValidateFinite(forward, nameof(forward));
        ValidateFinite(right, nameof(right));
        ValidateFinite(up, nameof(up));
        ValidateFinite(distance, nameof(distance));

        if (distance == 0 || (forward == 0 && right == 0 && up == 0))
            return sample;

        var pitch = DegreesToRadians(sample.Pitch);
        var yaw = DegreesToRadians(sample.Yaw);
        var roll = DegreesToRadians(sample.Roll);

        var sinPitch = Math.Sin(pitch);
        var cosPitch = Math.Cos(pitch);
        var sinYaw = Math.Sin(yaw);
        var cosYaw = Math.Cos(yaw);
        var sinRoll = Math.Sin(roll);
        var cosRoll = Math.Cos(roll);

        var forwardX = cosPitch * cosYaw;
        var forwardY = cosPitch * sinYaw;
        var forwardZ = -sinPitch;

        var rightX = (-sinRoll * sinPitch * cosYaw) + (cosRoll * sinYaw);
        var rightY = (-sinRoll * sinPitch * sinYaw) - (cosRoll * cosYaw);
        var rightZ = -sinRoll * cosPitch;

        var upX = (cosRoll * sinPitch * cosYaw) + (sinRoll * sinYaw);
        var upY = (cosRoll * sinPitch * sinYaw) - (sinRoll * cosYaw);
        var upZ = cosRoll * cosPitch;

        var moved = new CameraSample(
            sample.X + distance * ((forward * forwardX) + (right * rightX) + (up * upX)),
            sample.Y + distance * ((forward * forwardY) + (right * rightY) + (up * upY)),
            sample.Z + distance * ((forward * forwardZ) + (right * rightZ) + (up * upZ)),
            sample.Pitch,
            sample.Yaw,
            sample.Roll,
            sample.Fov);

        if (!moved.IsValid)
            throw new ArgumentOutOfRangeException(nameof(distance), "Movement produced an invalid camera sample.");

        return moved;
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180);

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite.");
    }
}
