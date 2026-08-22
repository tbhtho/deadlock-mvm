using System.IO.Pipes;
using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Native.InProcess;

/// <summary>Versioned, typed local IPC client for the replay-only in-process camera.</summary>
public sealed class NativeReplayCameraClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private ulong _sequence;
    private int _processId;

    public bool Connected => _pipe?.IsConnected == true;

    public async Task<InProcessCameraStatus> ConnectAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));
        if (Connected)
            throw new InvalidOperationException("The native replay camera is already connected.");

        var pipe = new NamedPipeClientStream(
            ".",
            $"DeadlockMVM.Native.{processId}",
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        try
        {
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            _pipe = pipe;
            _processId = processId;
            return await RequestAsync(
                InProcessMessageType.Hello,
                InProcessProtocol.SerializeHello(processId),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
            _processId = 0;
            throw;
        }
    }

    public Task<InProcessCameraStatus> HeartbeatAsync(
        bool replayActive,
        bool freeRoam,
        long replayTick,
        long gameTickOffset,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            InProcessMessageType.Heartbeat,
            InProcessProtocol.SerializeHeartbeat(replayActive, freeRoam, replayTick, gameTickOffset),
            cancellationToken);

    public Task<InProcessCameraStatus> SetCampathAsync(
        CampathPath path,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            InProcessMessageType.SetCampath,
            InProcessProtocol.SerializeCampath(path),
            cancellationToken);

    public Task<InProcessCameraStatus> PrepareCameraObservationAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(InProcessMessageType.PrepareCameraObservation, Array.Empty<byte>(), cancellationToken);

    public Task<InProcessCameraStatus> ClearCampathAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(InProcessMessageType.ClearCampath, Array.Empty<byte>(), cancellationToken);

    public Task<InProcessCameraStatus> SetCameraSampleAsync(
        CameraSample sample,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            InProcessMessageType.SetCameraSample,
            InProcessProtocol.SerializeCameraSample(sample),
            cancellationToken);

    public Task<InProcessCameraStatus> EnableOverrideAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(InProcessMessageType.EnableOverride, Array.Empty<byte>(), cancellationToken);

    public Task<InProcessCameraStatus> DisableOverrideAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(InProcessMessageType.DisableOverride, Array.Empty<byte>(), cancellationToken);

    public Task<InProcessCameraStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(InProcessMessageType.GetStatus, Array.Empty<byte>(), cancellationToken);

    public Task<InProcessCameraStatus> ShutdownAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(InProcessMessageType.Shutdown, Array.Empty<byte>(), cancellationToken);

    private async Task<InProcessCameraStatus> RequestAsync(
        InProcessMessageType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("The native replay camera is not connected.");
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sequence = unchecked(++_sequence);
            var message = InProcessProtocol.CreateMessage(type, sequence, payload.Span);
            await pipe.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

            var headerBytes = new byte[InProcessProtocol.HeaderSize];
            await ReadExactlyAsync(pipe, headerBytes, cancellationToken).ConfigureAwait(false);
            var header = InProcessProtocol.ParseHeader(headerBytes);
            if (header.Type != InProcessMessageType.Status || header.PayloadSize != InProcessProtocol.StatusSize ||
                header.Sequence != sequence)
                throw new InvalidDataException("Native status response does not match the request.");

            var statusBytes = new byte[header.PayloadSize];
            await ReadExactlyAsync(pipe, statusBytes, cancellationToken).ConfigureAwait(false);
            var status = InProcessProtocol.ParseStatus(statusBytes);
            if (status.ProcessId != _processId)
                throw new InvalidDataException("Native status came from an unexpected process.");
            return status;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Native replay camera disconnected.");
            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var pipe = _pipe;
        _pipe = null;
        _processId = 0;
        if (pipe is not null)
            await pipe.DisposeAsync().ConfigureAwait(false);
        _requestGate.Dispose();
    }
}
