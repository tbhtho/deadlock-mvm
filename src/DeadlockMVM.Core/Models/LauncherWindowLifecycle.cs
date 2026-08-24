namespace DeadlockMVM.Core.Models;

public enum LauncherWindowAction
{
    None,
    Hide,
    Restore,
}

/// <summary>
/// Pure launch-aware policy for the WPF shell. The managed host is deliberately
/// independent from window visibility and remains alive while Deadlock runs.
/// </summary>
public sealed class LauncherWindowLifecycle
{
    private static readonly TimeSpan DefaultLaunchObservationTimeout = TimeSpan.FromMinutes(2);
    private readonly TimeSpan _launchObservationTimeout;
    private DateTime _launchArmedAtUtc;
    private bool _launchArmed;

    public LauncherWindowLifecycle(TimeSpan? launchObservationTimeout = null)
    {
        _launchObservationTimeout = launchObservationTimeout ?? DefaultLaunchObservationTimeout;
        if (_launchObservationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(launchObservationTimeout));
    }

    public bool IsHidden { get; private set; }

    public void ArmSuccessfulLaunch(DateTime utcNow)
    {
        _launchArmedAtUtc = utcNow;
        _launchArmed = true;
    }

    public void CancelLaunch() => _launchArmed = false;

    public LauncherWindowAction Observe(bool deadlockRunning, bool hideEnabled, DateTime utcNow)
    {
        if (IsHidden && (!deadlockRunning || !hideEnabled))
        {
            IsHidden = false;
            _launchArmed = false;
            return LauncherWindowAction.Restore;
        }

        if (_launchArmed && utcNow - _launchArmedAtUtc > _launchObservationTimeout)
            _launchArmed = false;

        if (!_launchArmed)
            return LauncherWindowAction.None;

        if (!hideEnabled)
        {
            _launchArmed = false;
            return LauncherWindowAction.None;
        }

        if (!deadlockRunning)
            return LauncherWindowAction.None;

        _launchArmed = false;
        IsHidden = true;
        return LauncherWindowAction.Hide;
    }
}
