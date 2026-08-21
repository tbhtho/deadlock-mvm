using DeadlockMVM.Core.Contracts;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Writes timestamped, leveled entries to a local log file. Logging failures are
/// swallowed so they can never take the launcher down.
/// </summary>
public sealed class FileLogService : ILogService
{
    private readonly object _lock = new();

    public FileLogService(string? logFilePath = null)
        => LogFilePath = logFilePath ?? AppPaths.LogFile;

    public string LogFilePath { get; }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
            catch
            {
                // Intentionally ignored: logging must never crash the launcher.
            }
        }
    }
}
