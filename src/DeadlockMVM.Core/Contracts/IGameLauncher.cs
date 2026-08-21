using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Contracts;

/// <summary>Launches the Deadlock executable.</summary>
public interface IGameLauncher
{
    LaunchResult Launch(LaunchRequest request);
}
