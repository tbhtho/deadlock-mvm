namespace DeadlockMVM.Core.Services;

public readonly record struct DemoStartupObservation(
    bool ReplayActive,
    string ReplayIdentity,
    bool Paused,
    bool HudHidden,
    bool NativeConnected,
    bool ReplayClockReady,
    bool FreeCameraEstablished);

public readonly record struct DemoStartupDirectives(
    int Generation,
    bool PauseDemo,
    bool HideGameHud,
    bool EnterFreeCamera);

/// <summary>
/// One-shot startup policy for a confirmed demo. Startup guarantees are retried
/// while they are still being established, but are never reasserted after the
/// owner deliberately resumes, restores HUD, or exits Free Camera.
/// </summary>
public sealed class DemoStartupPolicy
{
    private static readonly TimeSpan PauseRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HudRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CameraRetryDelay = TimeSpan.FromMilliseconds(500);
    private const int MaxPauseAttempts = 3;
    private const int MaxHudAttempts = 6;
    private const int MaxCameraAttempts = 12;

    private readonly object _gate = new();
    private string? _replayIdentity;
    private int _generation;
    private int _pauseAttempts;
    private int _hudAttempts;
    private int _cameraAttempts;
    private bool _pauseCompleted;
    private bool _hudCompleted;
    private bool _hudAttemptInFlight;
    private bool _cameraCompleted;
    private bool _cameraAttemptInFlight;
    private bool _ownerOverrodeStartup;
    private DateTimeOffset _pauseRetryAfter = DateTimeOffset.MinValue;
    private DateTimeOffset _hudRetryAfter = DateTimeOffset.MinValue;
    private DateTimeOffset _cameraRetryAfter = DateTimeOffset.MinValue;

    public static string CreateReplayIdentity(string replayName, long? totalTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayName);
        _ = totalTicks;
        // Total ticks are normally learned after the name. Including that
        // late-arriving value would falsely create a second startup session
        // and could pause again after the owner has already pressed Resume.
        return replayName.Trim();
    }

    public DemoStartupDirectives Observe(
        DemoStartupObservation observation,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!observation.ReplayActive || string.IsNullOrWhiteSpace(observation.ReplayIdentity))
            {
                ResetSession();
                return new DemoStartupDirectives(_generation, false, false, false);
            }

            if (!string.Equals(_replayIdentity, observation.ReplayIdentity, StringComparison.Ordinal))
                BeginSession(observation.ReplayIdentity);

            if (_ownerOverrodeStartup)
                return new DemoStartupDirectives(_generation, false, false, false);

            if (observation.Paused)
                _pauseCompleted = true;
            if (observation.HudHidden)
                _hudCompleted = true;
            if (observation.FreeCameraEstablished)
                _cameraCompleted = true;

            var pause = false;
            if (!_pauseCompleted && _pauseAttempts < MaxPauseAttempts && now >= _pauseRetryAfter)
            {
                pause = true;
                _pauseAttempts++;
                _pauseRetryAfter = now + PauseRetryDelay;
            }

            var hideHud = false;
            if (!_hudCompleted && !_hudAttemptInFlight &&
                _hudAttempts < MaxHudAttempts && now >= _hudRetryAfter)
            {
                hideHud = true;
                _hudAttemptInFlight = true;
                _hudAttempts++;
            }

            var enterFreeCamera = false;
            if (_pauseCompleted && observation.NativeConnected && observation.ReplayClockReady &&
                !_cameraCompleted && !_cameraAttemptInFlight &&
                _cameraAttempts < MaxCameraAttempts && now >= _cameraRetryAfter)
            {
                enterFreeCamera = true;
                _cameraAttemptInFlight = true;
                _cameraAttempts++;
            }

            return new DemoStartupDirectives(
                _generation,
                pause,
                hideHud,
                enterFreeCamera);
        }
    }

    public void MarkPauseCommandFailed(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
                return;
            _pauseRetryAfter = DateTimeOffset.MinValue;
        }
    }

    public void MarkHudAttemptCompleted(
        int generation,
        bool success,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (generation != _generation)
                return;
            _hudAttemptInFlight = false;
            _hudCompleted = success;
            if (!success)
                _hudRetryAfter = now + HudRetryDelay;
        }
    }

    public void MarkFreeCameraAttemptCompleted(
        int generation,
        bool success,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (generation != _generation)
                return;
            _cameraAttemptInFlight = false;
            _cameraCompleted = success;
            if (!success)
                _cameraRetryAfter = now + CameraRetryDelay;
        }
    }

    public bool IsCurrent(int generation)
    {
        lock (_gate)
            return generation == _generation && _replayIdentity is not null &&
                !_ownerOverrodeStartup;
    }

    public void MarkOwnerOverride()
    {
        lock (_gate)
        {
            if (_replayIdentity is null)
                return;
            _ownerOverrodeStartup = true;
            _hudAttemptInFlight = false;
            _cameraAttemptInFlight = false;
        }
    }

    private void BeginSession(string replayIdentity)
    {
        _generation++;
        _replayIdentity = replayIdentity;
        _pauseAttempts = 0;
        _hudAttempts = 0;
        _cameraAttempts = 0;
        _pauseCompleted = false;
        _hudCompleted = false;
        _hudAttemptInFlight = false;
        _cameraCompleted = false;
        _cameraAttemptInFlight = false;
        _ownerOverrodeStartup = false;
        _pauseRetryAfter = DateTimeOffset.MinValue;
        _hudRetryAfter = DateTimeOffset.MinValue;
        _cameraRetryAfter = DateTimeOffset.MinValue;
    }

    private void ResetSession()
    {
        if (_replayIdentity is not null)
            _generation++;
        _replayIdentity = null;
        _pauseAttempts = 0;
        _hudAttempts = 0;
        _cameraAttempts = 0;
        _pauseCompleted = false;
        _hudCompleted = false;
        _hudAttemptInFlight = false;
        _cameraCompleted = false;
        _cameraAttemptInFlight = false;
        _ownerOverrodeStartup = false;
        _pauseRetryAfter = DateTimeOffset.MinValue;
        _hudRetryAfter = DateTimeOffset.MinValue;
        _cameraRetryAfter = DateTimeOffset.MinValue;
    }
}
