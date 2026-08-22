using System.Diagnostics;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;

namespace DeadlockMVM.Core.Native.InProcess;

/// <summary>
/// Application lifecycle for the explicitly loaded replay-only camera DLL.
/// VConsole remains responsible for replay and spectator commands.
/// </summary>
public sealed class NativeReplayCameraSession : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(200);
    private readonly ReplayController _controller;
    private readonly ICameraService _camera;
    private readonly ILogService _log;
    private readonly string _dllPath;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Task _monitorTask;
    private NativeReplayCameraClient? _client;
    private InProcessCameraStatus? _status;
    private string _message = "Native backend unavailable until replay playback is confirmed.";
    private bool _campathPlaying;

    public NativeReplayCameraSession(
        ReplayController controller,
        ICameraService camera,
        ILogService log,
        string dllPath)
    {
        _controller = controller;
        _camera = camera;
        _log = log;
        _dllPath = Path.GetFullPath(dllPath);
        _camera.SelectionChanged += OnSelectionChanged;
        _controller.SeekCompleted += OnSeekCompleted;
        _monitorTask = MonitorAsync(_stop.Token);
    }

    public event EventHandler? StatusChanged;

    public InProcessBackendState State => _status?.State ?? InProcessBackendState.Unavailable;
    public InProcessCameraStatus? Status => _status;
    public string Message => _message;
    public bool Connected => _client?.Connected == true;
    public bool Available => Connected && _status?.Flags.HasFlag(InProcessStatusFlags.Resolved) == true;
    public bool CampathPlaying => _campathPlaying;

    public async Task<InProcessCameraStatus> PlayLinearCampathAsync(
        LinearCampath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.IsValid)
            throw new ArgumentOutOfRangeException(nameof(path));

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _client ?? throw new InvalidOperationException(_message);
            if (!IsConfirmedReplay(_controller.State) || _controller.GameTickOffset is null)
                throw new InvalidOperationException("Replay playback and its tick calibration must be confirmed.");

            await _camera.EnterFreeRoamAsync(cancellationToken).ConfigureAwait(false);
            await SendHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
            UpdateStatus(await client.SetLinearCampathAsync(path, cancellationToken).ConfigureAwait(false));
            var status = await client.EnableOverrideAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(status);
            if (!status.OverrideActive || !status.Flags.HasFlag(InProcessStatusFlags.CampathActive))
                throw new InvalidOperationException($"Native Campath did not acquire the camera: {status.Error}.");

            await SeekToPathStartAsync(checked((int)path.From.DemoTick), cancellationToken).ConfigureAwait(false);
            await _camera.EnterFreeRoamAsync(cancellationToken).ConfigureAwait(false);
            status = await SendHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
            var readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!status.OverrideActive && DateTime.UtcNow < readyDeadline)
            {
                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
                status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus(status);
            }
            if (!status.OverrideActive)
                throw new InvalidOperationException($"Native Campath did not reacquire Free Roam: {status.Error}.");

            _campathPlaying = true;
            _controller.Play();
            _message = $"Linear Campath active: tick {path.From.DemoTick} to {path.To.DemoTick}.";
            _log.Info($"Native camera: {_message}");
            OnStatusChanged();
            return status;
        }
        catch
        {
            _campathPlaying = false;
            if (_client is { Connected: true } client)
            {
                try
                {
                    await client.DisableOverrideAsync(CancellationToken.None).ConfigureAwait(false);
                    await client.ClearCampathAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    _log.Warn($"Native camera: failed Play cleanup: {ex.Message}");
                }
            }
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopCampathAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCampathCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task StopCampathCoreAsync(CancellationToken cancellationToken)
    {
        var client = _client;
        _campathPlaying = false;
        if (client?.Connected == true)
        {
            try
            {
                UpdateStatus(await client.DisableOverrideAsync(cancellationToken).ConfigureAwait(false));
                await client.ClearCampathAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _log.Warn($"Native camera: release failed: {ex.Message}");
            }
        }

        _message = "Campath stopped; Deadlock owns the Free Roam camera.";
        OnStatusChanged();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            do
            {
                if (_client?.Connected != true)
                    await TryConnectAsync(cancellationToken).ConfigureAwait(false);
                else
                    await SendHeartbeatAsync(_client, cancellationToken).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _campathPlaying = false;
            if (_status is { } status)
                _status = status with { State = InProcessBackendState.Failed, Error = InProcessErrorCode.ProtocolError };
            var client = _client;
            _client = null;
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            _message = $"Native backend failed: {ex.Message}";
            _log.Warn(_message);
            OnStatusChanged();
        }
    }

    private async Task TryConnectAsync(CancellationToken cancellationToken)
    {
        var replay = _controller.State;
        if (!IsConfirmedReplay(replay) || _controller.GameTickOffset is null)
            return;
        if (!File.Exists(_dllPath))
        {
            _message = $"Native backend unavailable: {Path.GetFileName(_dllPath)} is missing.";
            OnStatusChanged();
            return;
        }

        using var process = FindDeadlockProcess();
        if (process is null)
            return;

        _message = "Native backend loading…";
        _status = new InProcessCameraStatus(
            InProcessBackendState.Loading, InProcessErrorCode.None, InProcessStatusFlags.None,
            process.Id, 0, 0, 0, -1, default);
        OnStatusChanged();

        var load = NativeReplayModuleLoader.LoadForReplay(process.Id, _dllPath);
        if (!load.Success)
        {
            _status = _status with { State = InProcessBackendState.Failed };
            _message = load.Message;
            _log.Warn($"Native camera: {load.Message}");
            OnStatusChanged();
            return;
        }

        var client = new NativeReplayCameraClient();
        try
        {
            var status = await client.ConnectAsync(process.Id, TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            _client = client;
            UpdateStatus(status);
            _message = status.Error == InProcessErrorCode.None
                ? "Native backend connected; camera override is armed only in Free Roam."
                : $"Native backend connected with error: {status.Error}.";
            _log.Info($"Native camera: {load.Message} Protocol v{InProcessProtocol.Version} connected.");
            await SendHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<InProcessCameraStatus> SendHeartbeatAsync(
        NativeReplayCameraClient client,
        CancellationToken cancellationToken)
    {
        var replay = _controller.State;
        var replayActive = IsConfirmedReplay(replay);
        var freeRoam = _camera.Selection.Mode == SpecCameraMode.FreeRoam;
        var status = await client.HeartbeatAsync(
            replayActive,
            freeRoam,
            replay.CurrentTick ?? -1,
            _controller.GameTickOffset ?? -1,
            cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        if (!replayActive && _campathPlaying)
        {
            _campathPlaying = false;
            _message = "Campath released because replay playback ended.";
            OnStatusChanged();
        }
        return status;
    }

    private async Task SeekToPathStartAsync(int tick, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, int completedTick)
        {
            if (Math.Abs((long)completedTick - tick) <= 2)
                completion.TrySetResult(completedTick);
        }

        _controller.SeekCompleted += OnCompleted;
        try
        {
            _controller.SeekToTick(tick);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controller.SeekCompleted -= OnCompleted;
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_campathPlaying && _camera.Selection.Mode != SpecCameraMode.FreeRoam)
            _ = StopAfterOwnershipLossAsync("Campath stopped because POV mode changed.");
    }

    private void OnSeekCompleted(object? sender, int tick)
    {
        if (_campathPlaying)
            _ = ReacquireAfterSeekAsync(tick);
    }

    private async Task ReacquireAfterSeekAsync(int tick)
    {
        try
        {
            await _camera.EnterFreeRoamAsync(_stop.Token).ConfigureAwait(false);
            if (_client is { Connected: true } client)
                await SendHeartbeatAsync(client, _stop.Token).ConfigureAwait(false);
            _log.Info($"Native camera: Free Roam reacquired after seek to tick {tick}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await StopAfterOwnershipLossAsync($"Campath stopped after seek recovery failed: {ex.Message}")
                .ConfigureAwait(false);
        }
    }

    private async Task StopAfterOwnershipLossAsync(string message)
    {
        try { await StopCampathAsync(_stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _message = message;
        _log.Warn($"Native camera: {message}");
        OnStatusChanged();
    }

    private void UpdateStatus(InProcessCameraStatus status)
    {
        _status = status;
        OnStatusChanged();
    }

    private void OnStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private static bool IsConfirmedReplay(ReplayState replay) =>
        replay.Connected && !string.IsNullOrWhiteSpace(replay.ReplayName) && replay.CurrentTick is not null &&
        (replay.TotalTicks is null || replay.CurrentTick < replay.TotalTicks);

    private static Process? FindDeadlockProcess() =>
        Process.GetProcessesByName("deadlock").FirstOrDefault() ??
        Process.GetProcessesByName("project8").FirstOrDefault();

    public async ValueTask DisposeAsync()
    {
        _camera.SelectionChanged -= OnSelectionChanged;
        _controller.SeekCompleted -= OnSeekCompleted;
        _stop.Cancel();
        try { await _monitorTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var client = _client;
            _client = null;
            if (client is not null)
            {
                try
                {
                    if (client.Connected)
                    {
                        await client.DisableOverrideAsync().ConfigureAwait(false);
                        await client.ShutdownAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    _log.Warn($"Native camera: shutdown response unavailable: {ex.Message}");
                }
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
            _stop.Dispose();
        }
    }
}
