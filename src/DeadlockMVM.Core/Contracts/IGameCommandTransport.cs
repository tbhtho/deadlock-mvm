namespace DeadlockMVM.Core.Contracts;

/// <summary>
/// Sends console commands to a running Deadlock process and receives its
/// console output. Implementations hide how the command actually reaches the
/// game; consumers only deal with editor-level intent.
/// </summary>
public interface IGameCommandTransport : IDisposable
{
    /// <summary>Gets a value indicating whether the transport is currently connected to the game.</summary>
    bool IsConnected { get; }

    /// <summary>Raised for every complete console output line produced by the game.</summary>
    event EventHandler<string>? OutputLineReceived;

    /// <summary>Raised when an established connection is lost.</summary>
    event EventHandler? Disconnected;

    /// <summary>Connects to the running game.</summary>
    /// <exception cref="TransportConnectException">Thrown when the game cannot be reached.</exception>
    void Connect(string host, int port);

    /// <summary>Closes the connection to the game.</summary>
    void Disconnect();

    /// <summary>Sends one console command to the game.</summary>
    /// <exception cref="TransportNotConnectedException">Thrown when no connection is established.</exception>
    void SendCommand(string command);
}

/// <summary>The game's command channel could not be reached.</summary>
public sealed class TransportConnectException : Exception
{
    public TransportConnectException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>A command was sent while no connection was established.</summary>
public sealed class TransportNotConnectedException : InvalidOperationException
{
    public TransportNotConnectedException()
        : base("Not connected to the game.")
    {
    }
}
