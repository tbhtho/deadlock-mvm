using System.Net.Sockets;
using System.Text;
using DeadlockMVM.Core.Contracts;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Command transport speaking the Source 2 VConsole2 protocol — the same
/// channel Deadlock's own <c>vconsole2.exe</c> uses (via <c>vconcomm.dll</c>,
/// TCP on localhost, default port 29000). This is a supported engine facility:
/// no injection, no patching, no memory access.
///
/// Wire format (12-byte chunk header):
///   char[4] type, uint32 version (big endian), uint16 length (big endian,
///   includes header), uint16 handle. Commands are "CMND" chunks with the
///   command text plus a trailing NUL; console output arrives as "PRNT"
///   chunks whose payload has a 28-byte prefix followed by text.
/// </summary>
public sealed class VConsoleTransport : IGameCommandTransport
{
    private const int HeaderLength = 12;
    private const byte ProtocolVersionHigh = 0x00;
    private const byte ProtocolVersionLow = 0xD4;

    private readonly object _gate = new();
    private readonly object _sendLock = new();
    private readonly MemoryStream _raw = new();
    private string _textBuffer = string.Empty;

    private TcpClient? _client;
    private Thread? _readerThread;
    private volatile bool _connected;
    private int _rawScan;

    public bool IsConnected => _connected;

    public event EventHandler<string>? OutputLineReceived;

    public event EventHandler? Disconnected;

    public void Connect(string host, int port)
    {
        Disconnect();

        var client = new TcpClient();
        try
        {
            client.Connect(host, port);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new TransportConnectException(
                $"Could not reach the Deadlock console at {host}:{port}.", ex);
        }

        // Publish the client, the reader thread and the connected flag together:
        // Connect and Disconnect are called from different threads (the poll
        // timer's auto-reconnect and the launcher's connection service), and
        // publishing them separately let a Disconnect dispose the new socket
        // while Connect then reported it as connected and started a reader on it.
        lock (_gate)
        {
            _client = client;
            _raw.SetLength(0);
            _rawScan = 0;
            _textBuffer = string.Empty;
            _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "DeadlockMVM.VConsole" };
            _connected = true;
            _readerThread.Start(client);
        }
    }

    public void Disconnect()
    {
        _connected = false;

        Thread? reader;
        lock (_gate)
        {
            reader = _readerThread;
            _readerThread = null;
            try { _client?.Dispose(); } catch { }
            _client = null;
        }

        reader?.Join(1000);
    }

    public void SendCommand(string command)
    {
        if (!_connected || _client is null)
            throw new TransportNotConnectedException();
        if (string.IsNullOrWhiteSpace(command))
            return;

        var payload = Encoding.ASCII.GetBytes(command);
        var total = HeaderLength + payload.Length + 1;
        // The chunk length is a 16-bit field. An oversized command used to wrap
        // silently and desynchronise the engine's stream; refuse it instead.
        if (total > ushort.MaxValue)
            throw new ArgumentException(
                $"A VConsole command must fit the 16-bit chunk length (limit {ushort.MaxValue - HeaderLength - 1} characters).",
                nameof(command));

        var message = new byte[total];
        Encoding.ASCII.GetBytes("CMND").CopyTo(message, 0);
        message[4] = ProtocolVersionHigh;
        message[5] = ProtocolVersionLow;
        message[8] = (byte)((total >> 8) & 0xFF);
        message[9] = (byte)(total & 0xFF);
        payload.CopyTo(message, HeaderLength);
        message[total - 1] = 0x00;

        try
        {
            // Serialize writes: the replay controller, camera poller, and UI
            // actions all send on this socket from different threads.
            lock (_sendLock)
            {
                var socket = _client.Client;
                var offset = 0;
                while (offset < message.Length)
                {
                    var sent = socket.Send(message, offset, message.Length - offset, SocketFlags.None);
                    if (sent <= 0)
                        throw new IOException("The VConsole socket closed while sending a command.");
                    offset += sent;
                }
            }
        }
        catch
        {
            HandleConnectionLost();
            throw new TransportNotConnectedException();
        }
    }

    public void Dispose() => Disconnect();

    private void ReadLoop(object? state)
    {
        var client = (TcpClient)state!;
        var buffer = new byte[65536];

        try
        {
            while (_connected)
            {
                var stream = client.GetStream();
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;

                lock (_gate)
                {
                    _raw.Write(buffer, 0, read);
                    DecodeChunks();
                    EmitCompleteLines();
                }
            }
        }
        catch
        {
            // Socket torn down or game exited.
        }

        if (_connected)
            HandleConnectionLost();
    }

    private void HandleConnectionLost()
    {
        if (!_connected)
            return;

        _connected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Scans accumulated bytes for protocol chunks. Invalid headers are skipped
    /// byte by byte (resync); partial chunks stay buffered until complete.
    /// </summary>
    private void DecodeChunks()
    {
        var data = _raw.ToArray();
        var pos = _rawScan;

        while (true)
        {
            if (pos + HeaderLength > data.Length)
                break;

            if (!IsChunkType(data, pos))
            {
                pos++;
                continue;
            }

            int length = (data[pos + 8] << 8) | data[pos + 9];
            if (length < HeaderLength)
            {
                pos++;
                continue;
            }

            if (pos + length > data.Length)
                break; // wait for the rest of the chunk

            var type = Encoding.ASCII.GetString(data, pos, 4);
            if (type == "PRNT" && length > HeaderLength + 28)
            {
                var text = Encoding.UTF8.GetString(data, pos + HeaderLength + 28, length - HeaderLength - 28);
                AppendText(text.TrimEnd('\0'));
            }

            pos += length;
        }

        _rawScan = pos;

        // Keep only the incomplete tail. Retaining decoded console history made
        // every read copy and allocate up to 4 MiB during replay loading.
        if (_rawScan > 0)
        {
            var rest = data[_rawScan..];
            _raw.SetLength(0);
            _raw.Write(rest);
            _rawScan = 0;
        }
    }

    private static bool IsChunkType(byte[] data, int pos)
    {
        for (var i = 0; i < 4; i++)
        {
            var b = data[pos + i];
            if (b is < (byte)'A' or > (byte)'Z')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Streaming text pipeline: PRNT payloads are appended to one persistent
    /// text buffer and only complete newline-terminated lines are emitted; a
    /// trailing partial line stays buffered for the next chunk. Chunks may
    /// fragment a line across chunks or coalesce many lines into one chunk.
    /// </summary>
    private void AppendText(string text)
    {
        _textBuffer += text;
    }

    private void EmitCompleteLines()
    {
        while (true)
        {
            var index = _textBuffer.IndexOf('\n');
            if (index < 0)
                break;

            var line = _textBuffer[..index].TrimEnd('\r').TrimEnd('\0');
            _textBuffer = _textBuffer[(index + 1)..];

            if (line.Length == 0)
                continue;

            OutputLineReceived?.Invoke(this, line);
        }
    }
}
