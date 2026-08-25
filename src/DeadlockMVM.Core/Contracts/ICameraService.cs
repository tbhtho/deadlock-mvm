using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;

namespace DeadlockMVM.Core.Contracts;

/// <summary>
/// Supported camera/POV operations exposed by the running replay client.
/// The interface deliberately reports capabilities so callers cannot present
/// unsupported state writes as if they were deterministic.
/// </summary>
public interface ICameraService : ICameraServicePollerSource
{
    CameraCapabilities Capabilities { get; }

    /// <summary>
    /// The spectator target/mode as tracked from MVM-issued commands. This is
    /// locally tracked, not engine-authoritative — see <see cref="SpectatorSelection"/>.
    /// </summary>
    SpectatorSelection Selection { get; }

    /// <summary>Raised after an MVM-issued command changes the tracked selection.</summary>
    event EventHandler? SelectionChanged;

    /// <summary>
    /// Raised when <see cref="Capabilities"/> may have changed — e.g. a native
    /// backend attached/detached, or the spectator mode changed what is writable.
    /// </summary>
    event EventHandler? CapabilitiesChanged;

    Task<CameraState?> ReadStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enters free roam from any state in one editor action. Reads the current
    /// transform so the roam entry keeps the camera where it is; returns the
    /// state that was read, or null when the transform was unavailable.
    /// </summary>
    Task<CameraState?> EnterFreeRoamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enters Free Roam while letting a replay owner linearize each synchronous
    /// VConsole mutation with its current replay lease. Implementations that
    /// contain awaits should override this so a replacement replay cannot
    /// receive the post-await camera command.
    /// </summary>
    Task<CameraState?> EnterFreeRoamIfCurrentAsync(
        Func<Action, bool> runIfCurrent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runIfCurrent);
        if (!runIfCurrent(static () => { }))
            throw new InvalidOperationException("Replay changed before Free Roam could be entered.");
        return EnterFreeRoamAsync(cancellationToken);
    }

    void SelectPlayer(string playerOrSlot);

    void SelectNextPlayer();

    void SelectPrevPlayer();

    void SelectInEye();

    void SelectChase();

    void MoveRoamTarget(double x, double y, double z);

    /// <summary>
    /// Moves the roaming camera to the requested position and returns the
    /// settled engine-reported state. Position only: rotation is not writable
    /// in this build.
    /// </summary>
    Task<CameraState?> GoToPositionAsync(double x, double y, double z, CancellationToken cancellationToken = default);

    void SetBaseFov(double fov);

    /// <summary>
    /// Writes the FOV of the camera actually being rendered. Only available when a
    /// backend has live-proven control of the active camera FOV (see Capabilities).
    /// </summary>
    Task<bool> SetActiveFovAsync(double fov, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the roaming camera's rotation (degrees). Free-roam only: follow modes
    /// derive their view from the observed target.
    /// </summary>
    Task<bool> SetCameraRotationAsync(double pitch, double yaw, double roll, CancellationToken cancellationToken = default);

    /// <summary>The shot captured by <see cref="SaveCameraAsync"/>, when one exists.</summary>
    CameraShot? SavedShot { get; }

    /// <summary>
    /// Captures the current transform and active FOV as a movie-camera shot.
    /// Null when Save/Restore is not currently supported (see Capabilities).
    /// </summary>
    Task<CameraShot?> SaveCameraAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings back the exact saved composition: position, rotation and FOV.
    /// Verifies the engine-reported result before reporting success.
    /// </summary>
    Task<bool> RestoreCameraAsync(CancellationToken cancellationToken = default);
}
