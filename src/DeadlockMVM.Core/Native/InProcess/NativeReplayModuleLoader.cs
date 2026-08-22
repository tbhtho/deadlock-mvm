using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeadlockMVM.Core.Native.InProcess;

public sealed record NativeModuleLoadResult(bool Success, bool AlreadyLoaded, string Message);

/// <summary>
/// Explicit Deadlock-only loader for the bundled replay camera DLL. This is not
/// an arbitrary module loader: process name and DLL filename are hard-gated.
/// </summary>
public static class NativeReplayModuleLoader
{
    private const string RequiredDllName = "DeadlockMVM.Native.dll";
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0;

    public static NativeModuleLoadResult LoadForReplay(int processId, string dllPath)
    {
        if (processId <= 0)
            return new NativeModuleLoadResult(false, false, "Deadlock process ID is invalid.");
        var fullPath = Path.GetFullPath(dllPath ?? string.Empty);
        if (!IsAllowedDllPath(fullPath) || !File.Exists(fullPath))
            return new NativeModuleLoadResult(false, false, $"Bundled native module not found: {fullPath}");

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return new NativeModuleLoadResult(false, false, "Deadlock process exited before native loading.");
        }
        using (process)
        {
            if (!IsAllowedProcessName(process.ProcessName))
                return new NativeModuleLoadResult(false, false, $"Refusing native load into '{process.ProcessName}'.");

            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (string.Equals(module.ModuleName, RequiredDllName, StringComparison.OrdinalIgnoreCase))
                        return new NativeModuleLoadResult(true, true, "Native replay camera is already loaded.");
                }
            }
            catch (Win32Exception ex)
            {
                return new NativeModuleLoadResult(false, false, $"Could not inspect Deadlock modules: {ex.Message}");
            }

            return LoadValidatedModule(process, fullPath);
        }
    }

    internal static bool IsAllowedProcessName(string? processName) =>
        processName is not null &&
        (processName.Equals("deadlock", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("project8", StringComparison.OrdinalIgnoreCase));

    internal static bool IsAllowedDllPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetFileName(path), RequiredDllName, StringComparison.OrdinalIgnoreCase);

    private static NativeModuleLoadResult LoadValidatedModule(Process process, string fullPath)
    {
        nint processHandle = 0;
        nint remotePath = 0;
        nint thread = 0;
        try
        {
            processHandle = OpenProcess(
                ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmWrite | ProcessVmRead,
                false,
                process.Id);
            if (processHandle == 0)
                return Fail("OpenProcess");

            var pathBytes = System.Text.Encoding.Unicode.GetBytes(fullPath + '\0');
            remotePath = VirtualAllocEx(processHandle, 0, (nuint)pathBytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remotePath == 0)
                return Fail("VirtualAllocEx");
            if (!WriteProcessMemory(processHandle, remotePath, pathBytes, (nuint)pathBytes.Length, out var written) ||
                written != (nuint)pathBytes.Length)
                return Fail("WriteProcessMemory");

            var localKernel = GetModuleHandleW("kernel32.dll");
            var localLoadLibrary = GetProcAddress(localKernel, "LoadLibraryW");
            if (localKernel == 0 || localLoadLibrary == 0)
                return Fail("GetProcAddress(LoadLibraryW)");

            nint remoteKernel = 0;
            foreach (ProcessModule module in process.Modules)
            {
                if (string.Equals(module.ModuleName, "kernel32.dll", StringComparison.OrdinalIgnoreCase))
                {
                    remoteKernel = module.BaseAddress;
                    break;
                }
            }
            if (remoteKernel == 0)
                return new NativeModuleLoadResult(false, false, "Deadlock kernel32.dll was not found.");
            var remoteLoadLibrary = remoteKernel + (localLoadLibrary - localKernel);

            thread = CreateRemoteThread(processHandle, 0, 0, remoteLoadLibrary, remotePath, 0, out _);
            if (thread == 0)
                return Fail("CreateRemoteThread");
            var wait = WaitForSingleObject(thread, 10_000);
            if (wait != WaitObject0)
                return new NativeModuleLoadResult(false, false, "Timed out loading the native replay camera.");
            if (!GetExitCodeThread(thread, out var exitCode) || exitCode == 0)
                return Fail("LoadLibraryW in Deadlock");

            return new NativeModuleLoadResult(true, false, "Native replay camera loaded explicitly.");
        }
        finally
        {
            if (thread != 0) CloseHandle(thread);
            if (remotePath != 0 && processHandle != 0) VirtualFreeEx(processHandle, remotePath, 0, MemRelease);
            if (processHandle != 0) CloseHandle(processHandle);
        }
    }

    private static NativeModuleLoadResult Fail(string operation) =>
        new(false, false, $"{operation} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateRemoteThread(
        nint process,
        nint threadAttributes,
        nuint stackSize,
        nint startAddress,
        nint parameter,
        uint creationFlags,
        out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThread(nint thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetProcAddress(nint module, [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
