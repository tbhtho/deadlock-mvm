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

    void SelectInEye();

    void SelectChase();

    void MoveRoamTarget(double x, double y, double z);

    void SetBaseFov(double fov);
}
