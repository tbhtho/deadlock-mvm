using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Continuously polls the camera transform from the running replay and raises
/// <see cref="StateUpdated"/> for each engine response. The loop is strictly
/// sequential — a new read never starts before the previous one finished —
/// and waits <see cref="Interval"/> between completed reads, so VConsole load
/// stays low. Start/Stop are driven by page visibility and connection state.
/// </summary>
public sealed class CameraStatePoller : IDisposable
{
    private readonly ICameraServicePollerSource _source;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public CameraStatePoller(ICameraServicePollerSource source, TimeSpan? interval = null)
    {
        _source = source;
        _interval = interval ?? TimeSpan.FromMilliseconds(300);
    }

    public event EventHandler<CameraState>? StateUpdated;

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning)
            return;

        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    public void Stop() => _cts?.Cancel();

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var state = await _source.ReadTransformAsync(cancellationToken).ConfigureAwait(false);
                if (state is not null)
                    StateUpdated?.Invoke(this, state);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Transport hiccup: keep polling until told to stop. The owner
                // stops the poller on disconnect.
            }

            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
