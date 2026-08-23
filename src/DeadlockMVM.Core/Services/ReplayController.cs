using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Editor-facing replay control: turns UI intents into engine commands over an
/// <see cref="IGameCommandTransport"/> and maintains engine-reported
/// <see cref="ReplayState"/>.
///
/// State strategy (verified against live Deadlock):
///  - position/name/total ticks: polled via the side-effect-free
///    <c>demo_gototick</c> usage dump ("Currently playing X of Y ticks").
///  - paused: derived authoritatively from stalled tick progression between
///    consecutive polls; "CGameRules - paused/unpaused" output lines provide
///    instant hints that the next poll confirms or corrects.
///  - timescale: the engine cannot report demo_timescale (host_timescale does
///    not track it), so the last commanded value is surfaced and null until
///    MVM sets one.
/// </summary>
public sealed class ReplayController : IReplayPlaybackState, IDisposable
{
    private readonly IGameCommandTransport _transport;
    private readonly ReplayStateParser _parser = new();
    private readonly object _gate = new();
    private readonly Timer _pollTimer;

    private ReplayState _state = ReplayState.Empty;
    private int? _lastPolledTick;
    private bool _hasLastPolledTick;
    private long _lastOutputUtcTicks;
    private bool _sentCommandSinceLastOutput;
    private string _host = "127.0.0.1";
    private int _port;
    private long _nextReconnectUtcTicks;
    private long _lastDemoInfoRequestUtcTicks;
    private bool _userDisconnected;
    private string _readyMarker = string.Empty;
    private volatile bool _ready;

    public ReplayController(IGameCommandTransport transport)
    {
        _transport = transport;
        _transport.OutputLineReceived += OnOutputLine;
        _transport.Disconnected += OnDisconnected;
        _pollTimer = new Timer(_ => PollPosition(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised whenever the known replay state changes.</summary>
    public event EventHandler<ReplayState>? StateChanged;

    /// <summary>Raised from authoritative engine output after a demo seek finishes.</summary>
    public event EventHandler<int>? SeekCompleted;

    public ReplayState State { get { lock (_gate) return _state; } }

    public bool IsConnected => _transport.IsConnected;

    /// <summary>Calibration required to convert the in-process game tick to a demo tick.</summary>
    public int? GameTickOffset => _parser.GameTickOffset;

    /// <summary>Interval between live position polls.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the connection may stay silent (no console output at all)
    /// before it is considered stale. Deadlock's VConsole serves a single
    /// client; if another tool took over or a ghost socket holds the slot,
    /// our polls stop being answered and the honest recovery is to drop and
    /// re-connect. Re-connection polls double as evictions: every command we
    /// send makes the engine produce output, which forces the server to
    /// notice dead peers and hand the slot to us.
    /// </summary>
    public TimeSpan OutputStaleTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Automatically re-connect after an unexpected disconnect.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Delay between automatic re-connect attempts.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public void Connect(string host, int port)
    {
        _host = host;
        _port = port;
        _userDisconnected = false;
        _transport.Connect(host, port);
        Interlocked.Exchange(ref _lastOutputUtcTicks, DateTime.UtcNow.Ticks);
        _sentCommandSinceLastOutput = false;
        ApplyConnected(true);

        // On connect the engine replays buffered console history to the new
        // client. Everything before our marker echo is history and must not
        // drive live state (it would flash stale pause/tick values).
        _ready = false;
        _readyMarker = $"MVM_READY_{Environment.TickCount64:X}";
        SafeSend($"echo {_readyMarker}");

        // Ask the engine who we are; results are applied once ready.
        SafeSend(ReplayCommands.QueryDemoInfo);
        Interlocked.Exchange(ref _lastDemoInfoRequestUtcTicks, DateTime.UtcNow.Ticks);
        StartPolling();
    }

    public void Disconnect()
    {
        _userDisconnected = true;
        StopPolling();
        _transport.Disconnect();
        ApplyConnected(false);
    }

    public void Pause() => Send(ReplayCommands.Pause);

    public void Play() => Send(ReplayCommands.Resume);

    public void TogglePause() => Send(ReplayCommands.TogglePause);

    public void SetSpeed(double speed)
    {
        ReplayCommands.ValidateSpeed(speed);
        Send(ReplayCommands.SetTimescale(speed));
        // Engine has no query for this; surface the commanded value explicitly.
        lock (_gate)
        {
            _state = _state with { Timescale = speed };
        }
        StateChanged?.Invoke(this, State);
    }

    public void SeekToTick(int tick) => Send(ReplayCommands.GotoTick(tick));

    public void StepTick(int count = 1) => Send(ReplayCommands.StepTicks(count));

    /// <summary>
    /// Steps one tick backwards. The engine has no reverse step command, so
    /// this seeks to the last known tick minus one; requires a known position.
    /// </summary>
    public bool StepBack()
    {
        var tick = State.CurrentTick;
        if (tick is not { } current || current <= 0)
            return false;

        SeekToTick(current - 1);
        return true;
    }

    public void ToggleDemoUi() => Send(ReplayCommands.ToggleDemoUi);

    /// <summary>Sends an arbitrary console command (advanced use).</summary>
    public void SendRaw(string command) => Send(command);

    public void Dispose()
    {
        _pollTimer.Dispose();
        _transport.OutputLineReceived -= OnOutputLine;
        _transport.Disconnected -= OnDisconnected;
        _transport.Dispose();
    }

    private void StartPolling()
        => _pollTimer.Change(PollInterval, PollInterval);

    private void StopPolling()
        => _pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private void PollPosition()
    {
        if (!_transport.IsConnected)
        {
            TryAutoReconnect();
            return;
        }

        // Staleness watchdog: commands sent but no engine output at all for
        // OutputStaleTimeout means another client owns the console (or the
        // connection died silently). Drop it; auto-reconnect re-establishes
        // and our polls then evict whoever holds the slot.
        if (_sentCommandSinceLastOutput &&
            DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastOutputUtcTicks) > OutputStaleTimeout.Ticks)
        {
            ScheduleReconnect();
            _transport.Disconnect();
            ApplyConnected(false);
            return;
        }

        if (!SafeSend(ReplayCommands.QueryPosition))
            return;

        _sentCommandSinceLastOutput = true;

        // VConsole can connect before +playdemo has finished loading. A one-shot
        // demo_info at connect then has no server_start_tick, which used to leave
        // the native backend unavailable for the entire otherwise healthy run.
        // Retry only while calibration is absent, at a bounded cadence, and stop
        // immediately once the engine supplies the real offset.
        var now = DateTime.UtcNow.Ticks;
        if (_ready && _parser.GameTickOffset is null &&
            now - Interlocked.Read(ref _lastDemoInfoRequestUtcTicks) >= TimeSpan.FromSeconds(2).Ticks)
        {
            if (SafeSend(ReplayCommands.QueryDemoInfo))
                Interlocked.Exchange(ref _lastDemoInfoRequestUtcTicks, now);
        }

        // The response arrives asynchronously via OnOutputLine; stall detection
        // happens once the reply updates CurrentTick (see MergeEngineState).
    }

    private void TryAutoReconnect()
    {
        if (_userDisconnected || !AutoReconnect)
            return;

        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _nextReconnectUtcTicks))
            return;

        try
        {
            Connect(_host, _port);
        }
        catch (Exception)
        {
            ScheduleReconnect();
        }
    }

    private void ScheduleReconnect()
        => Interlocked.Exchange(ref _nextReconnectUtcTicks, (DateTime.UtcNow + ReconnectDelay).Ticks);

    private void OnOutputLine(object? sender, string line)
    {
        Interlocked.Exchange(ref _lastOutputUtcTicks, DateTime.UtcNow.Ticks);

        if (!_ready)
        {
            if (_readyMarker.Length > 0 && line.Contains(_readyMarker))
                _ready = true;
            // Pre-marker lines are history: feed the parser only for hidden
            // calibration (server_start_tick), never for live state.
            _parser.ParseLine(line);
            return;
        }

        _sentCommandSinceLastOutput = false;

        var parsed = _parser.ParseLine(line);
        if (parsed is not null)
            MergeEngineState(parsed);
        if (_parser.TryParseSeekCompletedTick(line, out var seekTick))
            SeekCompleted?.Invoke(this, seekTick);
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        // Keep the poll timer running so auto-reconnect can drive recovery;
        // user-initiated Disconnect() already stopped it.
        _ready = false;
        _sentCommandSinceLastOutput = false;
        ApplyConnected(false);
    }

    private void MergeEngineState(ReplayState update)
    {
        ReplayState merged;

        lock (_gate)
        {
            var current = _state;
            merged = current with
            {
                Connected = true,
                IsPaused = update.IsPaused ?? current.IsPaused,
                CurrentTick = update.CurrentTick ?? current.CurrentTick,
                TotalTicks = update.TotalTicks ?? current.TotalTicks,
                Timescale = update.Timescale ?? current.Timescale,
                ReplayName = update.ReplayName ?? current.ReplayName,
            };

            if (update.CurrentTick is { } tick)
            {
                // Full position-poll replies carry tick + total + file name;
                // they are the authoritative heartbeat used for pause detection.
                var isPollReply = update.TotalTicks is not null && update.ReplayName is not null;

                if (isPollReply)
                {
                    var reachedEnd = merged.TotalTicks is { } total && tick >= total;
                    if (reachedEnd)
                    {
                        _hasLastPolledTick = false;
                        _lastPolledTick = null;
                    }
                    else if (_hasLastPolledTick)
                    {
                        // Only judge motion once a baseline exists; the very
                        // first reply must not claim "playing".
                        var stalled = _lastPolledTick == tick;

                        if (merged.IsPaused != stalled)
                            merged = merged with { IsPaused = stalled };

                        _lastPolledTick = tick;
                    }
                    else
                    {
                        _lastPolledTick = tick;
                        _hasLastPolledTick = true;
                    }
                }
                else
                {
                    // Seek/pause output lines reset the stall baseline so the
                    // next poll pair starts fresh.
                    _lastPolledTick = tick;
                    _hasLastPolledTick = true;
                }
            }

            _state = merged;
        }

        StateChanged?.Invoke(this, merged);
    }

    private void ApplyConnected(bool connected)
    {
        lock (_gate)
        {
            _state = _state with { Connected = connected };
            if (!connected)
            {
                _hasLastPolledTick = false;
                _lastPolledTick = null;
            }
        }

        StateChanged?.Invoke(this, State);
    }

    private bool SafeSend(string command)
    {
        try
        {
            _transport.SendCommand(command);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Send(string command)
    {
        _transport.SendCommand(command);
    }
}
