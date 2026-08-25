using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Supported camera discovery/control service for a running replay.
/// Full deterministic Save/Restore is intentionally not exposed yet because
/// this Deadlock build has no supported active-camera rotation writer and no
/// authoritative active-camera FOV query.
/// </summary>
public sealed class ReplayCameraService : ICameraService
{
    private static readonly TimeSpan SettlePollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

    private readonly IGameCommandTransport _transport;
    private SpectatorSelection _selection = new();

    public ReplayCameraService(IGameCommandTransport transport)
    {
        _transport = transport;
    }

    public CameraCapabilities Capabilities { get; } = new()
    {
        CanReadTransform = true,
        CanWritePosition = true,
        CanWriteRotation = false,
        CanSelectPlayer = true,
        CanReadBaseFov = true,
        CanWriteBaseFov = true,
        CanReadActiveFov = false,
        CanWriteActiveFov = false,
        CanSaveRestore = false,
        Limitation = "getpos reads the active transform and spec_goto writes roam position (landing citadel_camera_height above the target), but this Deadlock build exposes no supported roaming-rotation writer and no active rendered-camera FOV control. Live-tested and rejected: setang/setang_exact (sv_cheats 1), r_setpos, cam_command, cl_ent_setang, fov_desired, citadel_camera_hero_fov, default_fov."
    };

    public SpectatorSelection Selection => _selection;

    public event EventHandler? SelectionChanged;

    /// <summary>The VConsole backend's capabilities are static; this never fires.</summary>
    public event EventHandler? CapabilitiesChanged
    {
        add { }
        remove { }
    }

    /// <summary>Not supported over VConsole.</summary>
    public Task<bool> SetActiveFovAsync(double fov, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>Not supported over VConsole.</summary>
    public Task<bool> SetCameraRotationAsync(double pitch, double yaw, double roll, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public CameraShot? SavedShot => null;

    public Task<CameraShot?> SaveCameraAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<CameraShot?>(null);

    public Task<bool> RestoreCameraAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<CameraState?> ReadStateAsync(CancellationToken cancellationToken = default)
        => ReadLinesAsync(cancellationToken,
            CameraCommands.ReadActiveTransform,
            CameraCommands.ReadBaseFov,
            CameraCommands.ReadHeroFov,
            CameraCommands.ReadCameraHeight);

    /// <summary>Lightweight transform-only read used by the live poll loop.</summary>
    public Task<CameraState?> ReadTransformAsync(CancellationToken cancellationToken = default)
        => ReadLinesAsync(cancellationToken, CameraCommands.ReadActiveTransform);

    /// <summary>
    /// Enters free roam from any state in one editor action. Live-tested engine
    /// behavior: the auto-director hijacks manual camera
    /// control, so it is disabled first; spec_mode 6 is silently ignored while
    /// a live player is being followed, but spec_goto always forces roaming
    /// (and drops the follow target) in a single command. The roam target is
    /// therefore the camera's own current position (height-compensated), so
    /// entering roam does not visibly move the camera. If the transform cannot
    /// be read, a bare spec_mode 6 is sent as a best-effort fallback.
    /// </summary>
    public Task<CameraState?> EnterFreeRoamAsync(CancellationToken cancellationToken = default) =>
        EnterFreeRoamCoreAsync(
            static effect =>
            {
                effect();
                return true;
            },
            cancellationToken);

    public Task<CameraState?> EnterFreeRoamIfCurrentAsync(
        Func<Action, bool> runIfCurrent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runIfCurrent);
        return EnterFreeRoamCoreAsync(runIfCurrent, cancellationToken);
    }

    private async Task<CameraState?> EnterFreeRoamCoreAsync(
        Func<Action, bool> runIfCurrent,
        CancellationToken cancellationToken)
    {
        if (!runIfCurrent(() =>
            {
                EnsureConnected();
                Send(CameraCommands.DisableAutoDirector);
            }))
            throw new InvalidOperationException("Replay changed before Free Roam could be entered.");

        var state = await ReadTransformAsync(cancellationToken).ConfigureAwait(false);
        if (!runIfCurrent(() =>
            {
                EnsureConnected();
                if (state?.ActiveTransform is { } transform)
                {
                    var height = state.CameraHeight ?? 63;
                    Send(CameraCommands.MoveRoamTarget(transform.X, transform.Y, transform.Z - height));
                }
                else
                {
                    Send(CameraCommands.FreeRoam);
                }

                // Roaming has no follow target; the engine drops it on roam entry.
                TrackSelection(new SpectatorSelection { Mode = SpecCameraMode.FreeRoam });
            }))
            throw new InvalidOperationException("Replay changed while Free Roam was being entered.");
        return state;
    }

    public void SelectPlayer(string playerOrSlot)
    {
        EnsureConnected();
        Send(CameraCommands.DisableAutoDirector);
        Send(CameraCommands.SelectPlayer(playerOrSlot));
        TrackSelection(_selection with
        {
            PlayerSlot = int.TryParse(playerOrSlot?.Trim(), out var slot) ? slot : null,
            // Selecting a player from roaming enters a follow mode on the engine side.
            Mode = _selection.Mode == SpecCameraMode.FreeRoam ? SpecCameraMode.Chase : _selection.Mode,
        });
    }

    public void SelectNextPlayer()
    {
        EnsureConnected();
        Send(CameraCommands.DisableAutoDirector);

        // Roaming has no target: spec_next acquires one and the engine enters
        // a follow mode, so pin chase explicitly to keep the result deterministic.
        var fromRoam = _selection.Mode == SpecCameraMode.FreeRoam;
        Send(CameraCommands.NextPlayer);
        if (fromRoam)
            Send(CameraCommands.Chase);

        TrackSelection(new SpectatorSelection
        {
            PlayerSlot = (_selection.PlayerSlot ?? 0) + 1,
            Mode = fromRoam ? SpecCameraMode.Chase : _selection.Mode,
        });
    }

    public void SelectPrevPlayer()
    {
        EnsureConnected();
        Send(CameraCommands.DisableAutoDirector);

        var fromRoam = _selection.Mode == SpecCameraMode.FreeRoam;
        Send(CameraCommands.PrevPlayer);
        if (fromRoam)
            Send(CameraCommands.Chase);

        TrackSelection(new SpectatorSelection
        {
            PlayerSlot = Math.Max(1, (_selection.PlayerSlot ?? 2) - 1),
            Mode = fromRoam ? SpecCameraMode.Chase : _selection.Mode,
        });
    }

    public void SelectInEye()
    {
        EnsureConnected();
        Send(CameraCommands.DisableAutoDirector);

        // spec_in_eye is a no-op without a follow target (live-tested), and
        // roaming has none — acquire a target first, then force the mode.
        var acquireTarget = _selection.Mode == SpecCameraMode.FreeRoam;
        if (acquireTarget)
            Send(CameraCommands.NextPlayer);

        Send(CameraCommands.InEye);
        TrackSelection(new SpectatorSelection
        {
            Mode = SpecCameraMode.InEye,
            PlayerSlot = acquireTarget ? (_selection.PlayerSlot ?? 0) + 1 : _selection.PlayerSlot,
        });
    }

    public void SelectChase()
    {
        EnsureConnected();
        Send(CameraCommands.DisableAutoDirector);

        var acquireTarget = _selection.Mode == SpecCameraMode.FreeRoam;
        if (acquireTarget)
            Send(CameraCommands.NextPlayer);

        Send(CameraCommands.Chase);
        TrackSelection(new SpectatorSelection
        {
            Mode = SpecCameraMode.Chase,
            PlayerSlot = acquireTarget ? (_selection.PlayerSlot ?? 0) + 1 : _selection.PlayerSlot,
        });
    }

    public void MoveRoamTarget(double x, double y, double z)
        => Send(CameraCommands.MoveRoamTarget(x, y, z));

    public void SetBaseFov(double fov) => Send(CameraCommands.SetBaseFov(fov));

    /// <summary>
    /// Moves the roaming camera to a requested position. spec_goto lands the
    /// camera citadel_camera_height units above the target, so the request is
    /// compensated by the live height readback. The camera glides to the target,
    /// so completion is detected by polling getpos until the position is stable
    /// (two identical consecutive reads) rather than by a fixed delay. If the
    /// settled landing misses the target, the actual engine landing offset is
    /// measured and one corrected goto is issued (live-tested: the effective
    /// offset can exceed the documented height cvar; a systematic offset cancels
    /// exactly, while world-geometry slides still surface as an off-target
    /// landing for the caller to judge).
    /// </summary>
    public async Task<CameraState?> GoToPositionAsync(double x, double y, double z, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        Send(CameraCommands.DisableAutoDirector);

        var current = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        var height = current?.CameraHeight ?? 63;
        MoveRoamTarget(x, y, z - height);
        // spec_goto switches the spectator mode to roaming and drops the
        // follow target.
        TrackSelection(new SpectatorSelection { Mode = SpecCameraMode.FreeRoam });

        var settled = await WaitForSettleAsync(cancellationToken).ConfigureAwait(false);
        if (settled?.ActiveTransform is { } landed &&
            (Math.Abs(landed.X - x) > 1.0 || Math.Abs(landed.Y - y) > 1.0 || Math.Abs(landed.Z - z) > 1.0))
        {
            // One corrected goto: subtract the measured landing error.
            MoveRoamTarget(x - (landed.X - x), y - (landed.Y - y), z - (landed.Z - z) - height);
            settled = await WaitForSettleAsync(cancellationToken).ConfigureAwait(false);
        }

        return settled;
    }

    /// <summary>Polls getpos until the position is stable (two identical reads).</summary>
    private async Task<CameraState?> WaitForSettleAsync(CancellationToken cancellationToken)
    {
        CameraTransform? previous = null;
        var deadline = DateTime.UtcNow + SettleTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(SettlePollInterval, cancellationToken).ConfigureAwait(false);
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            var transform = state?.ActiveTransform;
            if (transform is null)
                continue;

            if (previous is { } last
                && last.X == transform.X && last.Y == transform.Y && last.Z == transform.Z)
            {
                return state;
            }

            previous = transform;
        }

        // Never settled within the timeout: report the last known state honestly.
        return await ReadStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureConnected()
    {
        if (!_transport.IsConnected)
            throw new TransportNotConnectedException();
    }

    private void Send(string command)
    {
        EnsureConnected();
        _transport.SendCommand(command);
    }

    private void TrackSelection(SpectatorSelection selection)
    {
        _selection = selection;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<CameraState?> ReadLinesAsync(CancellationToken cancellationToken, params string[] commands)
    {
        EnsureConnected();

        var begin = $"MVM_CAMERA_READ_{Environment.TickCount64:X}";
        var end = $"MVM_CAMERA_READ_END_{Environment.TickCount64:X}";
        var lines = new List<string>();
        var started = false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnLine(object? sender, string line)
        {
            // Transport output arrives on the transport thread while the awaiting
            // continuation may already be snapshotting — all access under the lock.
            lock (lines)
            {
                if (!started)
                {
                    if (line.Contains(begin, StringComparison.Ordinal))
                        started = true;
                    return;
                }

                if (line.Contains(end, StringComparison.Ordinal))
                {
                    completion.TrySetResult(true);
                    return;
                }

                lines.Add(line);
            }
        }

        _transport.OutputLineReceived += OnLine;
        try
        {
            _transport.SendCommand($"echo {begin}");
            foreach (var command in commands)
                _transport.SendCommand(command);
            _transport.SendCommand($"echo {end}");
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            string[] snapshot;
            lock (lines)
            {
                snapshot = lines.ToArray();
            }

            return CameraStateParser.Merge(snapshot);
        }
        finally
        {
            _transport.OutputLineReceived -= OnLine;
        }
    }
}
