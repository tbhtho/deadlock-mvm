namespace DeadlockMVM.Core.Contracts;

/// <summary>
/// Abstracts Windows registry reads so Steam detection can be unit-tested
/// without touching the real registry.
/// </summary>
public interface IRegistryReader
{
    string? ReadCurrentUser(string keyPath, string valueName);

    string? ReadLocalMachine(string keyPath, string valueName);

    string? ReadLocalMachine32(string keyPath, string valueName);
}
