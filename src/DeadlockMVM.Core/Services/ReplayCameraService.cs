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

    public Task<CameraState?> ReadStateAsync(CancellationToken cancellationToken = default)
        => ReadLinesAsync(cancellationToken,
            CameraCommands.ReadActiveTransform,
            CameraCommands.ReadBaseFov,
            CameraCommands.ReadHeroFov,
            CameraCommands.ReadCameraHeight);

    public void EnterFreeRoam() => Send(CameraCommands.FreeRoam);

    public void SelectPlayer(string playerOrSlot) => Send(CameraCommands.SelectPlayer(playerOrSlot));

    public void SelectNextPlayer() => Send(CameraCommands.NextPlayer);

    public void SelectPrevPlayer() => Send(CameraCommands.PrevPlayer);

    public void SelectInEye() => Send(CameraCommands.InEye);

    public void SelectChase() => Send(CameraCommands.Chase);

    public void MoveRoamTarget(double x, double y, double z)
        => Send(CameraCommands.MoveRoamTarget(x, y, z));

    public void SetBaseFov(double fov) => Send(CameraCommands.SetBaseFov(fov));

    /// <summary>
    /// Moves the roaming camera to a requested position. spec_goto lands the
    /// camera citadel_camera_height units above the target, so the request is
    /// compensated by the live height readback. The camera glides to the target,
    /// so completion is detected by polling getpos until the position is stable
    /// (two identical consecutive reads) rather than by a fixed delay.
    /// </summary>
    public async Task<CameraState?> GoToPositionAsync(double x, double y, double z, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var current = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        var height = current?.CameraHeight ?? 63;
        MoveRoamTarget(x, y, z - height);

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

        _transport.OutputLineReceived += OnLine;
        try
        {
            _transport.SendCommand($"echo {begin}");
            foreach (var command in commands)
                _transport.SendCommand(command);
            _transport.SendCommand($"echo {end}");
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return CameraStateParser.Merge(lines);
        }
        finally
        {
            _transport.OutputLineReceived -= OnLine;
        }
    }
}
