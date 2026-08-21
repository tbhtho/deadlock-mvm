namespace DeadlockMVM.Core.Models;

/// <summary>Precise active-view transform reported by the engine's getpos command.</summary>
public sealed record CameraTransform(
    double X,
    double Y,
    double Z,
    double Pitch,
    double Yaw,
    double Roll);

/// <summary>
/// Camera discovery snapshot. Base/hero FOV values are cvar readbacks, not an
/// assertion of the active rendered camera FOV; <see cref="ActiveFov"/> stays
/// unavailable until the engine exposes an authoritative camera-FOV query.
/// </summary>
public sealed record CameraState
{
    public CameraTransform? ActiveTransform { get; init; }

    public double? BaseFov { get; init; }

    public double? HeroFov { get; init; }

    public double? ActiveFov { get; init; }

    /// <summary>
    /// The citadel_camera_height cvar. spec_goto lands the roaming camera this
    /// many units above the requested point, so position writes compensate by it.
    /// </summary>
    public double? CameraHeight { get; init; }
}
