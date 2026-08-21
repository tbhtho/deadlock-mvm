using DeadlockMVM.Core.Contracts;
using Microsoft.Win32;

namespace DeadlockMVM.Core.Services;

/// <summary>Real registry reader backed by Microsoft.Win32.Registry.</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public string? ReadCurrentUser(string keyPath, string valueName)
        => Registry.GetValue($@"HKEY_CURRENT_USER\{keyPath}", valueName, null) as string;

    public string? ReadLocalMachine(string keyPath, string valueName)
        => Registry.GetValue($@"HKEY_LOCAL_MACHINE\{keyPath}", valueName, null) as string;

    public string? ReadLocalMachine32(string keyPath, string valueName)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = baseKey.OpenSubKey(keyPath);
        return key?.GetValue(valueName) as string;
    }
}
