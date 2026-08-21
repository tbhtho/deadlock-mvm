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
    private readonly IGameCommandTransport _transport;

    public ReplayCameraService(IGameCommandTransport transport)
    {
        _transport = transport;
    }

    public CameraCapabilities Capabilities { get; } = new()
    {
        CanReadActiveTransform = true,
        CanMoveRoamTarget = true,
        CanSelectPlayer = true,
        CanReadBaseFov = true,
        CanWriteBaseFov = true,
        CanWriteActiveRotation = false,
        CanReadActiveFov = false,
        CanSaveRestoreDeterministically = false,
        Limitation = "getpos reads the active transform, but current Deadlock exposes no supported command to write roaming rotation (pitch/yaw) or query the rendered camera FOV."
    };

    public async Task<CameraState?> ReadStateAsync(CancellationToken cancellationToken = default)
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
            _transport.SendCommand(CameraCommands.ReadActiveTransform);
            _transport.SendCommand(CameraCommands.ReadBaseFov);
            _transport.SendCommand(CameraCommands.ReadHeroFov);
            _transport.SendCommand($"echo {end}");
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return CameraStateParser.Merge(lines);
        }
        finally
        {
            _transport.OutputLineReceived -= OnLine;
        }
    }

    public void EnterFreeRoam() => Send(CameraCommands.FreeRoam);

    public void SelectPlayer(string playerOrSlot) => Send(CameraCommands.SelectPlayer(playerOrSlot));

    public void SelectInEye() => Send(CameraCommands.InEye);

    public void SelectChase() => Send(CameraCommands.Chase);

    public void MoveRoamTarget(double x, double y, double z)
        => Send(CameraCommands.MoveRoamTarget(x, y, z));

    public void SetBaseFov(double fov) => Send(CameraCommands.SetBaseFov(fov));

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
}
