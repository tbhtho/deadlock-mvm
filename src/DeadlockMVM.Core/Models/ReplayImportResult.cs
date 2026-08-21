namespace DeadlockMVM.Core.Models;

/// <summary>Outcome of importing a demo into Deadlock's replay directory.</summary>
public sealed class ReplayImportResult
{
    private ReplayImportResult()
    {
    }

    public bool Success { get; private init; }

    public string Message { get; private init; } = string.Empty;

    public ReplayInfo? Replay { get; private init; }

    public static ReplayImportResult Imported(ReplayInfo replay) => new()
    {
        Success = true,
        Replay = replay,
        Message = $"Imported {replay.FileName}.",
    };

    public static ReplayImportResult Failed(string message) => new()
    {
        Success = false,
        Message = message,
    };
}
