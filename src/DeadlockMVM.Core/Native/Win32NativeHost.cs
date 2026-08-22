using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeadlockMVM.Core.Native;

/// <summary>
/// Windows implementation of <see cref="INativeHost"/> using System.Diagnostics for
/// process/module discovery and OpenProcess + Read/WriteProcessMemory for access.
/// External memory access only — no injection, no drivers. Local replay tooling.
/// </summary>
internal sealed class Win32NativeHost : INativeHost
{
    public int? FindGameProcessId()
    {
        foreach (var name in new[] { DeadlockConstants.GameProcessName, DeadlockConstants.GameProcessNameAlt })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                    return processes[0].Id;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        return null;
    }

    public bool TryFindClientModule(int processId, out ulong moduleBase, out long moduleSize, out string modulePath)
    {
        moduleBase = 0;
        moduleSize = 0;
        modulePath = string.Empty;

        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            foreach (ProcessModule module in process.Modules)
            {
                // The Citadel game client — other Source 2 modules can share the name.
                if (!string.Equals(module.ModuleName, "client.dll", StringComparison.OrdinalIgnoreCase) ||
                    module.FileName.IndexOf("citadel", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                moduleBase = (ulong)module.BaseAddress;
                moduleSize = module.ModuleMemorySize;
                modulePath = module.FileName;
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Process exited between discovery and module enumeration, or access denied.
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    public IProcessMemory? OpenProcess(int processId)
        => Win32ProcessMemory.TryOpen(processId);

    private sealed class Win32ProcessMemory : IProcessMemory
    {
        private const uint AccessRights = ProcessVmRead | ProcessVmWrite | ProcessVmOperation | ProcessQueryInformation;
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessVmWrite = 0x0020;
        private const uint ProcessVmOperation = 0x0008;
        private const uint ProcessQueryInformation = 0x0400;

        private IntPtr _handle;

        private Win32ProcessMemory(IntPtr handle) => _handle = handle;

        public bool IsOpen => _handle != IntPtr.Zero;

        public static IProcessMemory? TryOpen(int processId)
        {
            var handle = OpenProcess(AccessRights, bInheritHandle: false, processId);
            return handle == IntPtr.Zero ? null : new Win32ProcessMemory(handle);
        }

        public bool TryRead(ulong address, Span<byte> buffer)
        {
            if (!IsOpen || buffer.IsEmpty)
                return false;

            var staging = new byte[buffer.Length];
            if (!ReadProcessMemory(_handle, (IntPtr)address, staging, (UIntPtr)staging.Length, out var read) ||
                read.ToUInt32() != staging.Length)
            {
                return false;
            }

            staging.CopyTo(buffer);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> buffer)
        {
            if (!IsOpen || buffer.IsEmpty)
                return false;

            var staging = buffer.ToArray();
            return WriteProcessMemory(_handle, (IntPtr)address, staging, (UIntPtr)staging.Length, out var written) &&
                   written.ToUInt32() == staging.Length;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(
            IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
