using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Contracts;

/// <summary>
/// Supported camera/POV operations exposed by the running replay client.
/// The interface deliberately reports capabilities so callers cannot present
/// unsupported state writes as if they were deterministic.
/// </summary>
public interface ICameraService
{
    CameraCapabilities Capabilities { get; }

    Task<CameraState?> ReadStateAsync(CancellationToken cancellationToken = default);

    void EnterFreeRoam();

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
