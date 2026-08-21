namespace DeadlockMVM.Core.Models;

/// <summary>Outcome of a launch attempt.</summary>
public sealed class LaunchResult
{
    private LaunchResult()
    {
    }

    public bool Success { get; private init; }

    /// <summary>Human-readable description of the outcome.</summary>
    public string Message { get; private init; } = string.Empty;

    /// <summary>Process id of the launched game, when successful.</summary>
    public int? ProcessId { get; private init; }

    /// <summary>The full command line that was (or would have been) executed.</summary>
    public string FullCommandLine { get; private init; } = string.Empty;

    public static LaunchResult Ok(int processId, string fullCommandLine) => new()
    {
        Success = true,
        ProcessId = processId,
        FullCommandLine = fullCommandLine,
        Message = $"Launched Deadlock (PID {processId}).",
    };

    public static LaunchResult Fail(string message) => new()
    {
        Success = false,
        Message = message,
    };
}
