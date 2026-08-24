using System.IO.Pipes;
using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Native.InProcess;

/// <summary>Versioned, typed local IPC client for the replay-only in-process camera.</summary>
public sealed class NativeReplayCameraClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _lifetimeLock = new();
    private NamedPipeClientStream? _pipe;
    private ulong _sequence;
    private int _processId;
    private int _disposed;

    public bool Connected => _pipe?.IsConnected == true;

    public async Task<InProcessCameraStatus> ConnectAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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
            lock (_lifetimeLock)
            {
                ThrowIfDisposed();
                if (_pipe is not null)
                    throw new InvalidOperationException("The native replay camera is already connected.");
                _processId = processId;
                _pipe = pipe;
            }
            return await RequestAsync(
                InProcessMessageType.Hello,
                InProcessProtocol.SerializeHello(processId),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Clear only this connection. A racing second ConnectAsync must not
            // erase a different pipe that already won publication.
            InvalidatePipe(pipe);
            await pipe.DisposeAsync().ConfigureAwait(false);
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
        CampathEndBehavior endBehavior = CampathEndBehavior.StopAndRelease,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            InProcessMessageType.SetCampath,
            InProcessProtocol.SerializeCampath(path, endBehavior),
            cancellationToken);

    public Task<InProcessCameraStatus> PrepareCameraObservationAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(InProcessMessageType.PrepareCameraObservation, Array.Empty<byte>(), cancellationToken);

    public Task<InProcessCameraStatus> ClearCampathAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(InProcessMessageType.ClearCampath, Array.Empty<byte>(), cancellationToken);

    public Task<InProcessCameraStatus> UpdateSmvmSnapshotAsync(
        SmvmSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            InProcessMessageType.UpdateSmvmSnapshot,
            InProcessProtocol.SerializeSmvmSnapshot(snapshot),
            cancellationToken);

    public Task<InProcessCameraStatus> SetEditorCampathAsync(
        IReadOnlyList<CampathKeyframe> keyframes,
        CampathInterpolationMode interpolation,
        CampathEasingMode easing,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            InProcessMessageType.SetEditorCampath,
            InProcessProtocol.SerializeEditorCampath(keyframes, interpolation, easing),
            cancellationToken);

    public Task<InProcessCameraStatus> ClearEditorCampathAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(InProcessMessageType.ClearEditorCampath, Array.Empty<byte>(), cancellationToken);

    public Task<InProcessCameraStatus> SetCampathDocumentsAsync(
        IReadOnlyList<CampathDocumentInfo> documents,
        CampathReplayIdentifier? currentReplay,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            InProcessMessageType.SetCampathDocuments,
            InProcessProtocol.SerializeCampathDocuments(documents, currentReplay),
            cancellationToken);

    public Task<InProcessCameraStatus> SetCameraSampleAsync(
        CameraSample sample,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            InProcessMessageType.SetCameraSample,
            InProcessProtocol.SerializeCameraSample(sample),
            cancellationToken);

    public Task<InProcessCameraStatus> SetRollOverrideAsync(
        double roll,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            InProcessMessageType.SetRollOverride,
            InProcessProtocol.SerializeRoll(roll),
            cancellationToken);

    public Task<InProcessCameraStatus> EnableManualCameraAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(InProcessMessageType.EnableManualCamera, Array.Empty<byte>(), cancellationToken);

    public Task<InProcessCameraStatus> DisableManualCameraAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(InProcessMessageType.DisableManualCamera, Array.Empty<byte>(), cancellationToken);

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
        ThrowIfDisposed();
        var pipe = _pipe ?? throw new InvalidOperationException("The native replay camera is not connected.");
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                ThrowIfDisposed();
                if (!ReferenceEquals(Volatile.Read(ref _pipe), pipe))
                    throw new InvalidOperationException("The native replay camera connection was retired.");
                var sequence = unchecked(++_sequence);
                var message = InProcessProtocol.CreateMessage(type, sequence, payload.Span);
                await pipe.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

                var headerBytes = new byte[InProcessProtocol.HeaderSize];
                await ReadExactlyAsync(pipe, headerBytes, cancellationToken).ConfigureAwait(false);
                var header = InProcessProtocol.ParseHeader(headerBytes);
                if (header.Type != InProcessMessageType.Status ||
                    header.PayloadSize != InProcessProtocol.StatusSize || header.Sequence != sequence)
                    throw new InvalidDataException("Native status response does not match the request.");

                var statusBytes = new byte[header.PayloadSize];
                await ReadExactlyAsync(pipe, statusBytes, cancellationToken).ConfigureAwait(false);
                var status = InProcessProtocol.ParseStatus(statusBytes);
                if (status.ProcessId != _processId)
                    throw new InvalidDataException("Native status came from an unexpected process.");
                return status;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // A failed/cancelled in-flight request can leave a response queued
                // and destroy framing. Retire this pipe; the monitor reconnects.
                InvalidatePipe(pipe);
                throw;
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private void InvalidatePipe(NamedPipeClientStream pipe)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _pipe, null, pipe), pipe))
            return;
        Volatile.Write(ref _processId, 0);
        pipe.Dispose();
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
        NamedPipeClientStream? pipe;
        lock (_lifetimeLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            pipe = Interlocked.Exchange(ref _pipe, null);
            Volatile.Write(ref _processId, 0);
        }
        if (pipe is not null)
            await pipe.DisposeAsync().ConfigureAwait(false);
        // Do not dispose the semaphore here: retiring the pipe deliberately
        // unblocks an in-flight request, and queued callers still need to
        // acquire/release the gate before observing the disposed state.
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(NativeReplayCameraClient));
    }
}
