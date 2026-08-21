namespace DeadlockMVM.Core.Contracts;

/// <summary>Appends timestamped entries to the local launcher log.</summary>
public interface ILogService
{
    string LogFilePath { get; }

    void Info(string message);

    void Warn(string message);

    void Error(string message);
}
