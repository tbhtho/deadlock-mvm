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

    Task<CameraState?> ReadStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enters free roam from any state in one editor action. Reads the current
    /// transform so the roam entry keeps the camera where it is; returns the
    /// state that was read, or null when the transform was unavailable.
    /// </summary>
    Task<CameraState?> EnterFreeRoamAsync(CancellationToken cancellationToken = default);

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
}
