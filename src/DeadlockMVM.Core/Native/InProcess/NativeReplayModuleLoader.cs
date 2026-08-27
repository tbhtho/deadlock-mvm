using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DeadlockMVM.Core.Services;

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
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const int ProcessCommandLineInformation = 60;
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> ProcessLoadGates = new();
    private static readonly ConcurrentDictionary<int, PendingRemoteLoad> PendingRemoteLoads = new();

    public static NativeModuleLoadResult LoadForReplay(
        int processId,
        string dllPath,
        string expectedExecutablePath)
    {
        if (processId <= 0)
            return new NativeModuleLoadResult(false, false, "Deadlock process ID is invalid.");
        if (!IsAllowedExecutablePath(expectedExecutablePath))
        {
            return new NativeModuleLoadResult(
                false,
                false,
                "Refusing native load: the selected Deadlock executable path is unavailable or invalid.");
        }
        var gate = ProcessLoadGates.GetOrAdd(processId, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            try
            {
                return LoadForReplaySerialized(processId, dllPath, expectedExecutablePath);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                       Win32Exception or UnauthorizedAccessException or IOException)
            {
                return new NativeModuleLoadResult(
                    false, false, $"Native replay camera load was rejected safely: {ex.Message}");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static NativeModuleLoadResult LoadForReplaySerialized(
        int processId,
        string dllPath,
        string expectedExecutablePath)
    {
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
            if (PendingRemoteLoads.TryRemove(processId, out var exited))
                ReleasePendingRemoteLoad(exited, freeRemotePath: false);
            return new NativeModuleLoadResult(false, false, "Deadlock process exited before native loading.");
        }
        using (process)
        {
            if (!IsAllowedProcessName(process.ProcessName))
                return new NativeModuleLoadResult(false, false, $"Refusing native load into '{process.ProcessName}'.");

            if (!IsEligibleReplayProcess(
                    process, expectedExecutablePath, out var eligibilityError))
                return new NativeModuleLoadResult(false, false, eligibilityError);

            var identity = new ReplayProcessIdentity(
                process.Id,
                process.StartTime.ToFileTimeUtc());
            var pendingResult = ReconcilePendingRemoteLoad(process, identity, fullPath);
            if (pendingResult is not null)
                return pendingResult;

            try
            {
                var loaded = FindLoadedModule(process, fullPath);
                if (loaded is not null)
                {
                    if (!loaded.Value.ExactPath)
                    {
                        return new NativeModuleLoadResult(
                            false,
                            true,
                            $"A native replay camera from a different path is already loaded: {loaded.Value.Path}. " +
                            "Restart Deadlock before loading this build.");
                    }
                    return new NativeModuleLoadResult(
                        true,
                        true,
                        "The validated native replay camera path is already loaded; protocol compatibility will be checked next.");
                }
            }
            catch (Win32Exception ex)
            {
                return new NativeModuleLoadResult(false, false, $"Could not inspect Deadlock modules: {ex.Message}");
            }

            return LoadValidatedModule(process, identity, fullPath);
        }
    }

    internal static bool IsAllowedProcessName(string? processName) =>
        processName is not null &&
        (processName.Equals("deadlock", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("project8", StringComparison.OrdinalIgnoreCase));

    internal static bool IsAllowedDllPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetFileName(path), RequiredDllName, StringComparison.OrdinalIgnoreCase);

    internal static bool IsAllowedExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var file = new FileInfo(Path.GetFullPath(path));
            if (!IsAllowedProcessName(Path.GetFileNameWithoutExtension(file.Name)))
                return false;
            var win64 = file.Directory;
            var bin = win64?.Parent;
            var game = bin?.Parent;
            return string.Equals(win64?.Name, "win64", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(bin?.Name, "bin", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(game?.Name, "game", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    internal static bool IsAllowedReplayCommandLine(string? commandLine) =>
        MovieModeLaunchPolicy.HasRequiredReplayArguments(CommandLine.Tokenize(commandLine));

    internal static bool IsEligibleReplayProcess(
        Process process,
        string expectedExecutablePath,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(process);
        error = string.Empty;
        if (process.HasExited || !IsAllowedProcessName(process.ProcessName))
        {
            error = "Refusing native load: the target is not a running Deadlock process.";
            return false;
        }
        var actualPath = process.MainModule?.FileName;
        if (!IsAllowedExecutablePath(actualPath))
        {
            error = $"Refusing native load from an unexpected executable path: {actualPath ?? "unavailable"}.";
            return false;
        }
        if (!IsAllowedExecutablePath(expectedExecutablePath))
        {
            error = "Refusing native load: the selected Deadlock executable path is unavailable or invalid.";
            return false;
        }
        if (!string.Equals(
                Path.GetFullPath(actualPath!),
                Path.GetFullPath(expectedExecutablePath),
                StringComparison.OrdinalIgnoreCase))
        {
            error = "Refusing native load: the observed Deadlock executable does not match the selected installation.";
            return false;
        }
        if (!TryReadCommandLine(process.Id, out var commandLine, out error))
            return false;
        if (IsAllowedReplayCommandLine(commandLine))
            return true;
        error = "Refusing native load: the target command line must contain exact -insecure and +playdemo <replay> arguments without -secure.";
        return false;
    }

    private static NativeModuleLoadResult LoadValidatedModule(
        Process process,
        ReplayProcessIdentity identity,
        string fullPath)
    {
        nint processHandle = 0;
        nint remotePath = 0;
        nint thread = 0;
        var remoteThreadStarted = false;
        var remoteThreadCompleted = false;
        try
        {
            processHandle = OpenProcess(
                ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmWrite | ProcessVmRead,
                false,
                process.Id);
            if (processHandle == 0)
                return Fail("OpenProcess");
            if (!GetProcessTimes(
                    processHandle,
                    out var creationTime,
                    out _,
                    out _,
                    out _))
                return Fail("GetProcessTimes");
            var openedIdentity = new ReplayProcessIdentity(
                process.Id,
                creationTime.ToInt64());
            if (openedIdentity != identity)
            {
                return new NativeModuleLoadResult(
                    false,
                    false,
                    "Refusing native load: the Deadlock process identity changed before injection.");
            }

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
            remoteThreadStarted = true;
            var wait = WaitForSingleObject(thread, 10_000);
            if (wait != WaitObject0)
            {
                // LoadLibraryW may still be reading remotePath. Transfer every live
                // handle into the pending record so a retry can poll this exact call
                // instead of freeing its argument or starting a duplicate call.
                PendingRemoteLoads[process.Id] = new PendingRemoteLoad(
                    identity,
                    fullPath,
                    processHandle,
                    remotePath,
                    thread);
                processHandle = 0;
                remotePath = 0;
                thread = 0;
                return new NativeModuleLoadResult(
                    false,
                    false,
                    wait == WaitTimeout
                        ? "Native replay camera load is still pending; its thread and remote path buffer were retained, and a second injection is blocked."
                        : "Native replay camera load completion is indeterminate; its thread and remote path buffer were retained, and a second injection is blocked.");
            }
            remoteThreadCompleted = true;
            if (!GetExitCodeThread(thread, out var exitCode) || exitCode == 0)
                return Fail("LoadLibraryW in Deadlock");

            process.Refresh();
            var loaded = FindLoadedModule(process, fullPath);
            if (loaded is null)
                return new NativeModuleLoadResult(false, false, "LoadLibraryW completed but the native module was not observable.");
            if (!loaded.Value.ExactPath)
            {
                return new NativeModuleLoadResult(
                    false,
                    true,
                    $"LoadLibraryW completed with a different native module path: {loaded.Value.Path}.");
            }
            return new NativeModuleLoadResult(true, false, "Native replay camera loaded explicitly and its exact path was verified.");
        }
        finally
        {
            if (thread != 0) CloseHandle(thread);
            if ((!remoteThreadStarted || remoteThreadCompleted) &&
                remotePath != 0 && processHandle != 0)
                VirtualFreeEx(processHandle, remotePath, 0, MemRelease);
            if (processHandle != 0) CloseHandle(processHandle);
        }
    }

    private static NativeModuleLoadResult? ReconcilePendingRemoteLoad(
        Process process,
        ReplayProcessIdentity identity,
        string requestedPath)
    {
        PendingRemoteLoads.TryGetValue(process.Id, out var pending);
        var disposition = PendingRemoteLoadPolicy.Reconcile(identity, pending?.Identity);
        if (disposition == PendingRemoteLoadDisposition.StartNew)
            return null;

        if (disposition == PendingRemoteLoadDisposition.RetireStale)
        {
            PendingRemoteLoads.TryRemove(process.Id, out var stale);
            if (stale is not null)
            {
                // A different start time means Windows has already destroyed the
                // old process address space. Closing our retained handles is enough;
                // attempting to free an address through a stale process is avoided.
                ReleasePendingRemoteLoad(stale, freeRemotePath: false);
            }
            return null;
        }

        return PollPendingRemoteLoad(process, requestedPath, pending!);
    }

    private static NativeModuleLoadResult PollPendingRemoteLoad(
        Process process,
        string requestedPath,
        PendingRemoteLoad pending)
    {
        var wait = WaitForSingleObject(pending.ThreadHandle, 0);
        var observation = wait switch
        {
            WaitObject0 => RemoteThreadPollObservation.Completed,
            WaitTimeout => RemoteThreadPollObservation.StillRunning,
            _ => RemoteThreadPollObservation.Indeterminate,
        };
        if (PendingRemoteLoadPolicy.MustRetainResources(observation))
        {
            var detail = wait == WaitFailed
                ? $" Polling the retained thread failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}"
                : string.Empty;
            return new NativeModuleLoadResult(
                false,
                false,
                "The earlier native replay camera load is still indeterminate; no second injection was attempted." + detail);
        }

        var gotExitCode = GetExitCodeThread(pending.ThreadHandle, out var exitCode);
        var exitCodeError = gotExitCode ? 0 : Marshal.GetLastWin32Error();
        PendingRemoteLoads.TryRemove(process.Id, out _);
        ReleasePendingRemoteLoad(pending, freeRemotePath: true);

        if (!gotExitCode)
        {
            return new NativeModuleLoadResult(
                false,
                false,
                $"Could not inspect the completed native replay camera load: {new Win32Exception(exitCodeError).Message}");
        }
        if (exitCode == 0)
            return new NativeModuleLoadResult(false, false, "LoadLibraryW in Deadlock returned no module.");

        process.Refresh();
        var loaded = FindLoadedModule(process, pending.FullPath);
        if (loaded is null)
        {
            return new NativeModuleLoadResult(
                false,
                false,
                $"The retained LoadLibraryW call for '{pending.FullPath}' completed, but the native module was not observable.");
        }
        if (!loaded.Value.ExactPath)
        {
            return new NativeModuleLoadResult(
                false,
                true,
                $"The retained LoadLibraryW call completed with a different native module path: {loaded.Value.Path}.");
        }
        if (!string.Equals(pending.FullPath, requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            return new NativeModuleLoadResult(
                false,
                true,
                $"The retained native replay camera loaded from '{pending.FullPath}', but this request selected '{requestedPath}'. " +
                "Restart Deadlock before loading a different build.");
        }
        return new NativeModuleLoadResult(
            true,
            false,
            "The retained native replay camera load completed and its exact path was verified; no duplicate injection was attempted.");
    }

    private static void ReleasePendingRemoteLoad(PendingRemoteLoad pending, bool freeRemotePath)
    {
        if (freeRemotePath && pending.RemotePath != 0 && pending.ProcessHandle != 0)
            VirtualFreeEx(pending.ProcessHandle, pending.RemotePath, 0, MemRelease);
        if (pending.ThreadHandle != 0)
            CloseHandle(pending.ThreadHandle);
        if (pending.ProcessHandle != 0)
            CloseHandle(pending.ProcessHandle);
    }

    private static (string Path, bool ExactPath)? FindLoadedModule(
        Process process,
        string expectedPath)
    {
        (string Path, bool ExactPath)? differentPath = null;
        foreach (ProcessModule module in process.Modules)
        {
            if (!string.Equals(module.ModuleName, RequiredDllName, StringComparison.OrdinalIgnoreCase))
                continue;
            var loadedPath = Path.GetFullPath(module.FileName);
            if (string.Equals(loadedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                return (loadedPath, true);
            differentPath ??= (loadedPath, false);
        }
        return differentPath;
    }

    private static bool TryReadCommandLine(
        int processId,
        out string commandLine,
        out string error)
    {
        commandLine = string.Empty;
        error = string.Empty;
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == 0)
        {
            error = $"Could not inspect the Deadlock command line: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        nint buffer = 0;
        try
        {
            _ = NtQueryInformationProcess(
                processHandle,
                ProcessCommandLineInformation,
                0,
                0,
                out var requiredLength);
            if (requiredLength <= Marshal.SizeOf<UnicodeString>())
            {
                error = "Could not determine the Deadlock command-line buffer size.";
                return false;
            }

            buffer = Marshal.AllocHGlobal(requiredLength);
            var status = NtQueryInformationProcess(
                processHandle,
                ProcessCommandLineInformation,
                buffer,
                requiredLength,
                out _);
            if (status < 0)
            {
                error = $"Could not inspect the Deadlock command line (NTSTATUS 0x{status:X8}).";
                return false;
            }

            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            var bufferStart = buffer.ToInt64();
            var textStart = value.Buffer.ToInt64();
            if (value.Length == 0 || (value.Length & 1) != 0 || value.Buffer == 0 ||
                textStart < bufferStart ||
                textStart - bufferStart > requiredLength - value.Length)
            {
                error = "Deadlock returned an invalid command-line record.";
                return false;
            }

            commandLine = Marshal.PtrToStringUni(value.Buffer, value.Length / sizeof(char)) ?? string.Empty;
            return commandLine.Length != 0;
        }
        finally
        {
            if (buffer != 0)
                Marshal.FreeHGlobal(buffer);
            CloseHandle(processHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly nint Buffer;
    }

    private sealed record PendingRemoteLoad(
        ReplayProcessIdentity Identity,
        string FullPath,
        nint ProcessHandle,
        nint RemotePath,
        nint ThreadHandle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _lowDateTime;
        private readonly uint _highDateTime;

        public long ToInt64() => unchecked((long)(((ulong)_highDateTime << 32) | _lowDateTime));
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        nint process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        nint processHandle,
        int processInformationClass,
        nint processInformation,
        int processInformationLength,
        out int returnLength);
}
