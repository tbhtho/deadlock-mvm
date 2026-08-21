namespace DeadlockMVM.Core.Models;

/// <summary>Everything the launcher needs to start a Deadlock MVM session.</summary>
public sealed class LaunchRequest
{
    /// <summary>Full path to project8.exe.</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>Working directory for the process (defaults to the exe directory).</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>Base arguments (the Movie Mode flags).</summary>
    public IReadOnlyList<string> BaseArguments { get; init; } = Array.Empty<string>();

    /// <summary>User-supplied extra arguments.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = Array.Empty<string>();
}
