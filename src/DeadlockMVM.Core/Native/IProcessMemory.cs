namespace DeadlockMVM.Core.Native;

/// <summary>
/// Minimal read/write access to another process's address space. Internal by
/// design: Deadlock MVM exposes movie-camera operations, never generic memory
/// primitives, outside the native backend.
/// </summary>
internal interface IProcessMemory : IDisposable
{
    bool IsOpen { get; }

    bool TryRead(ulong address, Span<byte> buffer);

    bool TryWrite(ulong address, ReadOnlySpan<byte> buffer);
}

/// <summary>Typed convenience accessors over <see cref="IProcessMemory"/>.</summary>
internal static class ProcessMemoryExtensions
{
    public static bool TryReadU64(this IProcessMemory memory, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (memory.TryRead(address, buffer))
        {
            value = BitConverter.ToUInt64(buffer);
            return true;
        }

        value = 0;
        return false;
    }

    public static bool TryReadF32(this IProcessMemory memory, ulong address, out float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (memory.TryRead(address, buffer))
        {
            value = BitConverter.ToSingle(buffer);
            return true;
        }

        value = 0;
        return false;
    }

    public static bool TryWriteF32(this IProcessMemory memory, ulong address, float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BitConverter.TryWriteBytes(buffer, value);
        return memory.TryWrite(address, buffer);
    }

    public static bool TryWrite3F32(this IProcessMemory memory, ulong address, float a, float b, float c)
    {
        Span<byte> buffer = stackalloc byte[12];
        BitConverter.TryWriteBytes(buffer, a);
        BitConverter.TryWriteBytes(buffer.Slice(4), b);
        BitConverter.TryWriteBytes(buffer.Slice(8), c);
        return memory.TryWrite(address, buffer);
    }
}
