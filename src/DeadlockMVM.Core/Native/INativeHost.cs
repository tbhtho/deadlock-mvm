namespace DeadlockMVM.Core.Native;

/// <summary>
/// OS-facing operations the native camera backend needs: find the game, locate
/// its client.dll, and open its address space. Abstracted so the backend's
/// attach/validation logic is unit-testable without a real process.
/// </summary>
internal interface INativeHost
{
    /// <summary>PID of the running Deadlock process, or null when not running.</summary>
    int? FindGameProcessId();

    /// <summary>Locates the Citadel client.dll module inside the process.</summary>
    bool TryFindClientModule(int processId, out ulong moduleBase, out long moduleSize, out string modulePath);

    /// <summary>Opens the process for read/write access; null when access is denied/gone.</summary>
    IProcessMemory? OpenProcess(int processId);
}
