namespace DeadlockMVM.Core.Models;

/// <summary>
/// A saved movie-camera composition: full transform plus the active rendered FOV.
/// Restoring a shot must bring the exact framing back — position and rotation are
/// mandatory; FOV is part of the shot because it defines the composition.
/// </summary>
public sealed record CameraShot(CameraTransform Transform, double Fov);
