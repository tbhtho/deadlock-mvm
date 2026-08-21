using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Contracts;

/// <summary>The transform-read capability CameraStatePoller needs; satisfied by ICameraService.</summary>
public interface ICameraServicePollerSource
{
    Task<CameraState?> ReadTransformAsync(CancellationToken cancellationToken = default);
}
