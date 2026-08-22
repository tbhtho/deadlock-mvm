using System.Buffers.Binary;
using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Native.InProcess;

internal enum InProcessMessageType : ushort
{
    Hello = 1,
    Heartbeat = 2,
    EnableOverride = 3,
    DisableOverride = 4,
    SetCameraSample = 5,
    GetStatus = 6,
    Shutdown = 7,
    SetCampath = 8,
    ClearCampath = 9,
    PrepareCameraObservation = 10,
    Status = 100,
}

public enum InProcessBackendState : uint
{
    Unavailable = 0,
    Loading = 1,
    Connected = 2,
    Ready = 3,
    Failed = 4,
}

public enum InProcessErrorCode : uint
{
    None = 0,
    WrongProcess = 1,
    ReplayLaunchRequired = 2,
    ClientModuleMissing = 3,
    SignatureMissing = 4,
    SignatureAmbiguous = 5,
    CameraUnavailable = 6,
    HookTargetMismatch = 7,
    HookInstallFailed = 8,
    ProtocolError = 9,
    ReplayGateClosed = 10,
    InvalidSample = 11,
    ObserverNotRoaming = 12,
    HeartbeatStale = 13,
    HookRuntimeInvalid = 14,
    ReplayClockUnavailable = 15,
}

[Flags]
public enum InProcessStatusFlags : uint
{
    None = 0,
    Resolved = 1 << 0,
    HookInstalled = 1 << 1,
    PipeConnected = 1 << 2,
    ReplayGate = 1 << 3,
    OverrideRequested = 1 << 4,
    OverrideActive = 1 << 5,
    HasSample = 1 << 6,
    CommandLineReplay = 1 << 7,
    CampathActive = 1 << 8,
    CameraObserved = 1 << 9,
}

public sealed record InProcessCameraStatus(
    InProcessBackendState State,
    InProcessErrorCode Error,
    InProcessStatusFlags Flags,
    int ProcessId,
    ulong AcceptedSequence,
    ulong AppliedSequence,
    ulong HookCalls,
    long ReplayTick,
    CameraSample Camera)
{
    public bool Ready => State == InProcessBackendState.Ready &&
                         Flags.HasFlag(InProcessStatusFlags.HookInstalled);
    public bool OverrideActive => Flags.HasFlag(InProcessStatusFlags.OverrideActive);
    public bool CameraObserved => Flags.HasFlag(InProcessStatusFlags.CameraObserved) && Camera.IsValid && ReplayTick >= 0;
}

internal static class InProcessProtocol
{
    public const uint Magic = 0x4D564D43;
    public const ushort Version = 3;
    public const int HeaderSize = 20;
    public const int StatusSize = 104;
    public const int CameraSampleSize = 56;
    public const int CampathKeyframeSize = 64;
    public const int CampathHeaderSize = 16;
    public const int MaxPayloadSize = CampathHeaderSize + (CampathKeyframeSize * CampathPath.MaxKeyframes);

    public static byte[] CreateMessage(InProcessMessageType type, ulong sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
            throw new ArgumentOutOfRangeException(nameof(payload));

        var message = new byte[HeaderSize + payload.Length];
        var span = message.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(span[12..], sequence);
        payload.CopyTo(span[HeaderSize..]);
        return message;
    }

    public static (InProcessMessageType Type, int PayloadSize, ulong Sequence) ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderSize)
            throw new InvalidDataException("Native response header has the wrong size.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException("Native response magic does not match.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != Version)
            throw new InvalidDataException("Native response version does not match.");
        var type = (InProcessMessageType)BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        if (!Enum.IsDefined(type))
            throw new InvalidDataException("Native response type is unknown.");
        var payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]));
        if (payloadSize is < 0 or > MaxPayloadSize)
            throw new InvalidDataException("Native response payload is too large.");
        return (type, payloadSize, BinaryPrimitives.ReadUInt64LittleEndian(header[12..]));
    }

    public static byte[] SerializeHello(int processId)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)processId));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), Version);
        return payload;
    }

    public static byte[] SerializeHeartbeat(bool replayActive, bool freeRoam, long replayTick, long gameTickOffset)
    {
        var payload = new byte[24];
        payload[0] = replayActive ? (byte)1 : (byte)0;
        payload[1] = freeRoam ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), replayTick);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16), gameTickOffset);
        return payload;
    }

    public static byte[] SerializeLinearCampath(LinearCampath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.IsValid)
            throw new ArgumentOutOfRangeException(nameof(path));
        return SerializeCampath(new CampathPath(new[] { path.From, path.To }));
    }

    public static byte[] SerializeCampath(CampathPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.IsValid)
            throw new ArgumentOutOfRangeException(nameof(path));

        var payload = new byte[CampathHeaderSize + (CampathKeyframeSize * path.Keyframes.Count)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)path.Keyframes.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)path.Interpolation);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)path.Easing);
        for (var index = 0; index < path.Keyframes.Count; index++)
            WriteKeyframe(payload, CampathHeaderSize + (index * CampathKeyframeSize), path.Keyframes[index]);
        return payload;
    }

    public static byte[] SerializeCameraSample(CameraSample sample)
    {
        if (!sample.IsValid)
            throw new ArgumentOutOfRangeException(nameof(sample));
        var payload = new byte[CameraSampleSize];
        WriteDouble(payload, 0, sample.X);
        WriteDouble(payload, 8, sample.Y);
        WriteDouble(payload, 16, sample.Z);
        WriteDouble(payload, 24, sample.Pitch);
        WriteDouble(payload, 32, sample.Yaw);
        WriteDouble(payload, 40, sample.Roll);
        WriteDouble(payload, 48, sample.Fov);
        return payload;
    }

    public static InProcessCameraStatus ParseStatus(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != StatusSize)
            throw new InvalidDataException("Native status payload has the wrong size.");
        return new InProcessCameraStatus(
            (InProcessBackendState)BinaryPrimitives.ReadUInt32LittleEndian(payload),
            (InProcessErrorCode)BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
            (InProcessStatusFlags)BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[12..])),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[32..]),
            BinaryPrimitives.ReadInt64LittleEndian(payload[40..]),
            new CameraSample(
                ReadDouble(payload, 48),
                ReadDouble(payload, 56),
                ReadDouble(payload, 64),
                ReadDouble(payload, 72),
                ReadDouble(payload, 80),
                ReadDouble(payload, 88),
                ReadDouble(payload, 96)));
    }

    private static void WriteDouble(Span<byte> payload, int offset, double value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(payload[offset..], BitConverter.DoubleToUInt64Bits(value));

    private static double ReadDouble(ReadOnlySpan<byte> payload, int offset) =>
        BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(payload[offset..]));

    private static void WriteKeyframe(Span<byte> payload, int offset, CampathKeyframe keyframe)
    {
        BinaryPrimitives.WriteInt64LittleEndian(payload[offset..], keyframe.DemoTick);
        SerializeCameraSample(keyframe.Camera).CopyTo(payload[(offset + 8)..]);
    }
}
