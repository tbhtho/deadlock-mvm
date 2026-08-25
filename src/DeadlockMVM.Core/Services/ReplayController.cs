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
///  - timescale: queried from the engine after every connection. A canonical
///    SMVM-commanded host_timescale is surfaced immediately, then reconciled
///    with the following engine readback.
/// </summary>
public sealed class ReplayController : IReplayPlaybackState, IDisposable
{
    private readonly IGameCommandTransport _transport;
    private readonly ReplayStateParser _parser = new();
    private readonly object _gate = new();
    private readonly object _telemetryGate = new();
    private readonly Timer _pollTimer;

    private ReplayState _state = ReplayState.Empty;
    private double? _lastKnownTimescale;
    private int? _lastPolledTick;
    private bool _hasLastPolledTick;
    private int _consecutiveStalledPolls;
    private long _lastOutputUtcTicks;
    private bool _sentCommandSinceLastOutput;
    private string _host = "127.0.0.1";
    private int _port;
    private long _nextReconnectUtcTicks;
    private long _lastDemoInfoRequestUtcTicks;
    private bool _userDisconnected;
    private string _readyMarker = string.Empty;
    private long _connectionGeneration;
    private long _authoritativeTelemetryGeneration;
    private long _replaySessionGeneration;
    private string? _lastConfirmedReplayIdentity;
    private bool _replayLoadPending;
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

    /// <summary>Current marker-fenced VConsole telemetry generation.</summary>
    public long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);

    /// <summary>
    /// Latest connection generation that produced a full post-marker replay
    /// position response (tick, total, and file name).
    /// </summary>
    public long AuthoritativeTelemetryGeneration =>
        Interlocked.Read(ref _authoritativeTelemetryGeneration);

    /// <summary>
    /// Captures replay identity and its VConsole generation under the same
    /// telemetry fence used by reconnects and process-boundary resets. Callers
    /// that issue an asynchronous replay operation can use this snapshot to
    /// reject delayed output from an older connection or demo load.
    /// </summary>
    public (long ConnectionGeneration, ReplayState State) CaptureReplayTelemetrySnapshot()
    {
        lock (_telemetryGate)
        {
            lock (_gate)
                return (_connectionGeneration, _state);
        }
    }

    /// <summary>Calibration required to convert the in-process game tick to a demo tick.</summary>
    public int? GameTickOffset
    {
        get { lock (_telemetryGate) return _parser.GameTickOffset; }
    }

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
        _ = BeginConnectionGeneration();
        _transport.Connect(host, port);
        Interlocked.Exchange(ref _lastOutputUtcTicks, DateTime.UtcNow.Ticks);
        _sentCommandSinceLastOutput = false;
        ApplyConnected(true);

        // On connect the engine replays buffered console history to the new
        // client. Everything before our marker echo is history and must not
        // drive live state (it would flash stale pause/tick values).
        SafeSend($"echo {_readyMarker}");

        // Recover the engine-owned playback speed after every connection. The
        // marker is queued first so its FIFO echo makes this readback live
        // state rather than buffered history; do not re-command a speed here.
        SafeSend(ReplayCommands.QueryTimescale);

        // Ask the engine who we are; results are applied once ready.
        SafeSend(ReplayCommands.QueryDemoInfo);
        Interlocked.Exchange(ref _lastDemoInfoRequestUtcTicks, DateTime.UtcNow.Ticks);
        StartPolling();
    }

    /// <summary>
    /// Invalidates cached replay telemetry at a newly observed Deadlock process
    /// boundary and issues a fresh marker-fenced discovery pass. The returned
    /// generation is safe for coordinators to use as a quarantine threshold.
    /// </summary>
    public long BeginProcessBoundaryTelemetryFence()
    {
        var generation = BeginConnectionGeneration();
        if (!_transport.IsConnected)
        {
            ApplyConnected(false);
            return generation;
        }

        Interlocked.Exchange(ref _lastOutputUtcTicks, DateTime.UtcNow.Ticks);
        _sentCommandSinceLastOutput = false;
        ApplyConnected(true);
        SafeSend($"echo {_readyMarker}");
        SafeSend(ReplayCommands.QueryTimescale);
        SafeSend(ReplayCommands.QueryDemoInfo);
        Interlocked.Exchange(ref _lastDemoInfoRequestUtcTicks, DateTime.UtcNow.Ticks);
        if (SafeSend(ReplayCommands.QueryPosition))
            _sentCommandSinceLastOutput = true;
        return generation;
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

    public bool PauseIfCurrent(ReplayCommandLease lease) =>
        SendReplayCommandIfCurrent(ReplayCommands.Pause, lease);

    public bool PlayIfCurrent(ReplayCommandLease lease) =>
        SendReplayCommandIfCurrent(ReplayCommands.Resume, lease);

    public bool TogglePauseIfCurrent(ReplayCommandLease lease) =>
        SendReplayCommandIfCurrent(ReplayCommands.TogglePause, lease);

    public bool IsReplayCommandLeaseCurrent(ReplayCommandLease lease)
    {
        lock (_telemetryGate)
            return IsReplayCommandLeaseCurrentLocked(lease);
    }

    /// <summary>
    /// Linearizes a synchronous editor effect with replay-session publication.
    /// The effect either completes on the captured replay or does not run.
    /// </summary>
    public bool RunIfCurrent(ReplayCommandLease lease, Action effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        lock (_telemetryGate)
        {
            if (!IsReplayCommandLeaseCurrentLocked(lease))
                return false;
            effect();
            return true;
        }
    }

    public void SetSpeed(double speed)
    {
        var canonical = ReplayCommands.CanonicalizeSpeed(speed);
        Send(ReplayCommands.SetTimescale(canonical));
        // Surface the commanded value immediately; the replay snapshot stays
        // consistent with the replay-speed controls without a console poll.
        lock (_gate)
        {
            _lastKnownTimescale = canonical;
            _state = _state with { Timescale = canonical };
        }
        StateChanged?.Invoke(this, State);
        // Ask the engine to confirm/clamp the value. Actual readback, when
        // supplied, merges after the requested state instead of being
        // overwritten by it.
        SafeSend(ReplayCommands.QueryTimescale);
    }

    public bool SetSpeedIfCurrent(double speed, ReplayCommandLease lease)
    {
        var canonical = ReplayCommands.CanonicalizeSpeed(speed);
        lock (_telemetryGate)
        {
            if (!IsReplayCommandLeaseCurrentLocked(lease))
                return false;
            Send(ReplayCommands.SetTimescale(canonical));
            lock (_gate)
            {
                _lastKnownTimescale = canonical;
                _state = _state with { Timescale = canonical };
            }
        }
        StateChanged?.Invoke(this, State);
        SafeSend(ReplayCommands.QueryTimescale);
        return true;
    }

    public void SeekToTick(int tick) => Send(ReplayCommands.GotoTick(tick));

    /// <summary>
    /// Issues a seek only while the caller's exact connection/demo identity is
    /// still current. Holding the telemetry fence through the transport write
    /// prevents a process reset or same-file reload from slipping between the
    /// final lease check and command issuance.
    /// </summary>
    internal bool SeekToTickIfCurrent(
        int tick,
        long expectedConnectionGeneration,
        long expectedReplaySessionGeneration,
        string expectedReplayName)
    {
        lock (_telemetryGate)
        {
            ReplayState current;
            lock (_gate)
                current = _state;

            if (expectedConnectionGeneration <= 0 ||
                expectedConnectionGeneration != _connectionGeneration ||
                expectedReplaySessionGeneration <= 0 ||
                expectedReplaySessionGeneration != current.ReplaySessionGeneration ||
                !current.Connected ||
                current.CurrentTick is null ||
                current.TotalTicks is { } totalTicks && current.CurrentTick >= totalTicks ||
                string.IsNullOrWhiteSpace(current.ReplayName) ||
                !string.Equals(
                    current.ReplayName.Trim(),
                    expectedReplayName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            Send(ReplayCommands.GotoTick(tick));
            return true;
        }
    }

    private bool SendReplayCommandIfCurrent(string command, ReplayCommandLease lease)
    {
        lock (_telemetryGate)
        {
            if (!IsReplayCommandLeaseCurrentLocked(lease))
                return false;
            Send(command);
            return true;
        }
    }

    private bool IsReplayCommandLeaseCurrentLocked(ReplayCommandLease lease)
    {
        ReplayState current;
        lock (_gate)
            current = _state;
        return lease.ConnectionGeneration > 0 &&
               lease.ConnectionGeneration == _connectionGeneration &&
               lease.ReplaySessionGeneration > 0 &&
               lease.ReplaySessionGeneration == current.ReplaySessionGeneration &&
               current.Connected &&
               current.CurrentTick is not null &&
               !string.IsNullOrWhiteSpace(current.ReplayName) &&
               !string.IsNullOrWhiteSpace(lease.ReplayName) &&
               string.Equals(
                   current.ReplayName.Trim(),
                   lease.ReplayName.Trim(),
                   StringComparison.OrdinalIgnoreCase);
    }

    public void StepTick(int count = 1) => Send(ReplayCommands.StepTicks(count));

    public bool StepTickIfCurrent(ReplayCommandLease lease, int count = 1) =>
        SendReplayCommandIfCurrent(ReplayCommands.StepTicks(count), lease);

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
        bool needsCalibration;
        lock (_telemetryGate)
            needsCalibration = _ready && _parser.GameTickOffset is null;
        if (needsCalibration &&
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
        ReplayState? changedState = null;
        int? completedSeekTick = null;

        lock (_telemetryGate)
        {
            if (!_ready)
            {
                if (_readyMarker.Length > 0 && line.Contains(_readyMarker))
                {
                    _ready = true;
                    _sentCommandSinceLastOutput = false;
                }
                // Every other pre-marker line is buffered history from an older
                // VConsole generation. It must not calibrate or publish this demo.
                return;
            }

            if (_parser.TryParseReplaySessionStarted(line, out var replayName))
            {
                changedState = BeginReplaySession(replayName);
            }
            else
            {
                _sentCommandSinceLastOutput = false;

                var parsed = _parser.ParseLine(line);
                if (parsed is not null)
                    changedState = MergeEngineState(parsed);
                if (_parser.TryParseSeekCompletedTick(line, out var parsedSeekTick))
                    completedSeekTick = parsedSeekTick;
            }
        }

        if (changedState is not null)
            StateChanged?.Invoke(this, changedState);
        if (completedSeekTick is { } seekTick)
            SeekCompleted?.Invoke(this, seekTick);
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        // Keep the poll timer running so auto-reconnect can drive recovery;
        // user-initiated Disconnect() already stopped it.
        lock (_telemetryGate)
        {
            _ready = false;
            _sentCommandSinceLastOutput = false;
        }
        ApplyConnected(false);
    }

    private ReplayState MergeEngineState(ReplayState update)
    {
        ReplayState merged;

        lock (_gate)
        {
            var current = _state;
            var isPositionReply = update.CurrentTick is not null &&
                                  update.TotalTicks is not null &&
                                  update.ReplayName is not null;
            var sessionBoundary = false;
            var boundaryCameFromLoadMarker = false;
            if (isPositionReply)
            {
                var confirmedIdentity = CreateConfirmedReplayIdentity(
                    update.ReplayName!,
                    update.TotalTicks!.Value);
                if (_replayLoadPending)
                {
                    _replayLoadPending = false;
                    boundaryCameFromLoadMarker = true;
                    sessionBoundary = true;
                    _lastConfirmedReplayIdentity = confirmedIdentity;
                }
                else if (_lastConfirmedReplayIdentity is null)
                {
                    if (_replaySessionGeneration == 0)
                        _replaySessionGeneration = 1;
                    _lastConfirmedReplayIdentity = confirmedIdentity;
                }
                else if (!string.Equals(
                             _lastConfirmedReplayIdentity,
                             confirmedIdentity,
                             StringComparison.OrdinalIgnoreCase))
                {
                    _replaySessionGeneration++;
                    _lastConfirmedReplayIdentity = confirmedIdentity;
                    sessionBoundary = true;
                }
            }

            var replayChanged = sessionBoundary ||
                                (current.ReplayName is not null &&
                                 update.ReplayName is not null &&
                                 (!string.Equals(
                                      current.ReplayName,
                                      update.ReplayName,
                                      StringComparison.OrdinalIgnoreCase) ||
                                  (current.TotalTicks is not null && update.TotalTicks is not null &&
                                   current.TotalTicks != update.TotalTicks)));
            if (replayChanged && !boundaryCameFromLoadMarker)
            {
                _parser.ResetGameTickOffset();
                Interlocked.Exchange(ref _lastDemoInfoRequestUtcTicks, 0);
                _hasLastPolledTick = false;
                _lastPolledTick = null;
                _consecutiveStalledPolls = 0;
            }
            merged = current with
            {
                Connected = true,
                IsPaused = update.IsPaused ?? (sessionBoundary ? null : current.IsPaused),
                CurrentTick = update.CurrentTick ?? current.CurrentTick,
                TotalTicks = update.TotalTicks ?? current.TotalTicks,
                Timescale = update.Timescale ?? current.Timescale,
                ReplayName = update.ReplayName ?? current.ReplayName,
                ReplaySessionGeneration = _replaySessionGeneration,
            };
            if (update.Timescale is { } timescale)
                _lastKnownTimescale = timescale;

            if (update.CurrentTick is { } tick)
            {
                // Full position-poll replies carry tick + total + file name;
                // they are the authoritative heartbeat used for pause detection.
                var isPollReply = update.TotalTicks is not null && update.ReplayName is not null;

                if (isPollReply)
                {
                    Interlocked.Exchange(
                        ref _authoritativeTelemetryGeneration,
                        ConnectionGeneration);
                    var reachedEnd = merged.TotalTicks is { } total && tick >= total;
                    if (reachedEnd)
                    {
                        _hasLastPolledTick = false;
                        _lastPolledTick = null;
                        _consecutiveStalledPolls = 0;
                    }
                    else if (_hasLastPolledTick)
                    {
                        // Only judge motion once a baseline exists; the very
                        // first reply must not claim "playing".
                        var stalled = _lastPolledTick == tick;
                        if (stalled)
                        {
                            _consecutiveStalledPolls++;
                            // A 1% replay advances only 0.64 integer ticks per
                            // one-second poll. Require two stalled intervals so
                            // ordinary sub-tick playback cannot impersonate a
                            // pause while explicit engine pause hints stay fast.
                            if (_consecutiveStalledPolls >= 2 && merged.IsPaused != true)
                                merged = merged with { IsPaused = true };
                        }
                        else
                        {
                            _consecutiveStalledPolls = 0;
                            if (merged.IsPaused != false)
                                merged = merged with { IsPaused = false };
                        }

                        _lastPolledTick = tick;
                    }
                    else
                    {
                        _lastPolledTick = tick;
                        _hasLastPolledTick = true;
                        _consecutiveStalledPolls = 0;
                    }
                }
                else
                {
                    // Seek/pause output lines reset the stall baseline so the
                    // next poll pair starts fresh.
                    _lastPolledTick = tick;
                    _hasLastPolledTick = true;
                    _consecutiveStalledPolls = 0;
                }
            }

            _state = merged;
        }

        return merged;
    }

    private void ApplyConnected(bool connected)
    {
        if (connected)
        {
            lock (_gate)
                _state = _state with { Connected = true };
        }
        else
        {
            lock (_telemetryGate)
            {
                _parser.ResetGameTickOffset();
                lock (_gate)
                {
                    _state = ReplayState.Empty with
                    {
                        Timescale = _lastKnownTimescale,
                        ReplaySessionGeneration = _replaySessionGeneration,
                    };
                    _hasLastPolledTick = false;
                    _lastPolledTick = null;
                    _consecutiveStalledPolls = 0;
                }
            }
        }

        StateChanged?.Invoke(this, State);
    }

    private long BeginConnectionGeneration()
    {
        lock (_telemetryGate)
        {
            // Generate the marker and invalidate all replay identity/calibration
            // atomically against output parsing. A callback that entered under
            // the old generation finishes before this reset; one that arrives
            // later cannot stamp stale data with the new generation.
            _ready = false;
            var generation = Interlocked.Increment(ref _connectionGeneration);
            _readyMarker =
                $"MVM_READY_{Environment.ProcessId:X}_{generation:X}_{Environment.TickCount64:X}";
            _parser.ResetGameTickOffset();
            _replayLoadPending = false;
            Interlocked.Exchange(ref _lastDemoInfoRequestUtcTicks, 0);
            lock (_gate)
            {
                _state = ReplayState.Empty with
                {
                    Timescale = _lastKnownTimescale,
                    ReplaySessionGeneration = _replaySessionGeneration,
                };
                _hasLastPolledTick = false;
                _lastPolledTick = null;
                _consecutiveStalledPolls = 0;
            }
            return generation;
        }
    }

    private ReplayState BeginReplaySession(string replayName)
    {
        ReplayState started;
        lock (_gate)
        {
            _replaySessionGeneration++;
            _replayLoadPending = true;
            _parser.ResetGameTickOffset();
            Interlocked.Exchange(ref _lastDemoInfoRequestUtcTicks, 0);
            _hasLastPolledTick = false;
            _lastPolledTick = null;
            _consecutiveStalledPolls = 0;
            _state = ReplayState.Empty with
            {
                Connected = true,
                Timescale = _lastKnownTimescale,
                ReplayName = replayName,
                ReplaySessionGeneration = _replaySessionGeneration,
            };
            started = _state;
        }
        return started;
    }

    private static string CreateConfirmedReplayIdentity(string replayName, int totalTicks)
    {
        _ = totalTicks;
        // The engine can correct total ticks after discovery. The activation
        // marker is authoritative for same-file reloads; without one, a stable
        // file name is safer than turning a metadata correction into a session.
        return replayName.Trim();
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

public readonly record struct ReplayCommandLease(
    long ConnectionGeneration,
    long ReplaySessionGeneration,
    string ReplayName);
