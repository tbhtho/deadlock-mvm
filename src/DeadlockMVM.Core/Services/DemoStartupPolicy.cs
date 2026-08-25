namespace DeadlockMVM.Core.Services;

public readonly record struct DemoStartupObservation(
    bool Connected,
    bool ReplayActive,
    string ReplayIdentity,
    bool Paused,
    bool HudHidden,
    bool NativeConnected,
    bool ReplayClockReady,
    bool FreeCameraEstablished,
    bool ReplayTelemetryAuthoritative = true);

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
    private string? _pendingOwnerOverrideIdentity;
    private bool _ownerOverridePendingForNextSession;
    private DateTimeOffset _pauseRetryAfter = DateTimeOffset.MinValue;
    private DateTimeOffset _hudRetryAfter = DateTimeOffset.MinValue;
    private DateTimeOffset _cameraRetryAfter = DateTimeOffset.MinValue;

    public static string CreateReplayIdentity(
        string replayName,
        long? totalTicks,
        long replaySessionGeneration = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayName);
        _ = totalTicks;
        // Total ticks are normally learned after the name. Including that
        // late-arriving value would falsely create a second startup session
        // and could pause again after the owner has already pressed Resume.
        var normalizedName = replayName.Trim();
        return replaySessionGeneration > 0
            ? $"{replaySessionGeneration}:{normalizedName}"
            : normalizedName;
    }

    public DemoStartupDirectives Observe(
        DemoStartupObservation observation,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            // A transient command-channel outage or the connected-but-empty
            // post-marker discovery window does not prove that the demo ended.
            // Preserve the same-demo session and every owner override until
            // live replay telemetry is authoritative again.
            if (!observation.Connected || !observation.ReplayTelemetryAuthoritative)
                return new DemoStartupDirectives(_generation, false, false, false);

            if (!observation.ReplayActive || string.IsNullOrWhiteSpace(observation.ReplayIdentity))
            {
                ResetSession();
                return new DemoStartupDirectives(_generation, false, false, false);
            }

            if (!string.Equals(_replayIdentity, observation.ReplayIdentity, StringComparison.Ordinal))
                BeginSession(observation.ReplayIdentity);
            else if (_ownerOverridePendingForNextSession)
            {
                // Provisional telemetry resolved back to the current replay.
                // Consume the wildcard here so it cannot suppress a later,
                // genuinely new demo after already protecting this session.
                _ownerOverrodeStartup = true;
                _ownerOverridePendingForNextSession = false;
                _pendingOwnerOverrideIdentity = null;
            }

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
            if (observation.NativeConnected && !_hudCompleted && !_hudAttemptInFlight &&
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

    /// <summary>
    /// Binds an explicit owner choice to the replay visible at input ingress.
    /// The coordinator can observe a new replay/process before this policy's
    /// dispatcher pass begins that session, so a mismatched identity is retained
    /// for the next BeginSession instead of being applied to the old session.
    /// A null identity during provisional telemetry targets the current session,
    /// or the next session when a process reset has already cleared it.
    /// </summary>
    public void MarkOwnerOverride(
        string? replayIdentity = null,
        bool scopeToNextObservedSession = false)
    {
        lock (_gate)
        {
            var targetIdentity = string.IsNullOrWhiteSpace(replayIdentity)
                ? null
                : replayIdentity.Trim();
            if (scopeToNextObservedSession)
            {
                _ownerOverridePendingForNextSession = true;
                if (_replayIdentity is not null)
                {
                    _ownerOverrodeStartup = true;
                    _hudAttemptInFlight = false;
                    _cameraAttemptInFlight = false;
                }
                return;
            }

            if (_replayIdentity is not null &&
                (targetIdentity is null || string.Equals(
                    _replayIdentity,
                    targetIdentity,
                    StringComparison.Ordinal)))
            {
                _ownerOverrodeStartup = true;
                _hudAttemptInFlight = false;
                _cameraAttemptInFlight = false;
                return;
            }

            if (_replayIdentity is null)
                _ownerOverridePendingForNextSession = true;
            else if (targetIdentity is not null)
            {
                _pendingOwnerOverrideIdentity = targetIdentity;
                // The owner acted on an authoritative replacement before its
                // dispatcher pass began that session. Retain the target for the
                // replacement and immediately retire every directive from the
                // old session so it cannot cross the boundary behind the input.
                _ownerOverrodeStartup = true;
                _hudAttemptInFlight = false;
                _cameraAttemptInFlight = false;
            }
        }
    }

    /// <summary>
    /// A new Deadlock process is a new physical demo session even when it loads
    /// the same replay file. Retire prior owner overrides and startup completion;
    /// transient same-process transport reconnects must not call this.
    /// </summary>
    public void ResetForNewProcess()
    {
        lock (_gate)
            ResetSession();
    }

    private void BeginSession(string replayIdentity)
    {
        var ownerOverridePending = _ownerOverridePendingForNextSession ||
            string.Equals(
                _pendingOwnerOverrideIdentity,
                replayIdentity,
                StringComparison.Ordinal);
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
        _ownerOverrodeStartup = ownerOverridePending;
        _pendingOwnerOverrideIdentity = null;
        _ownerOverridePendingForNextSession = false;
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
        _pendingOwnerOverrideIdentity = null;
        _ownerOverridePendingForNextSession = false;
        _pauseRetryAfter = DateTimeOffset.MinValue;
        _hudRetryAfter = DateTimeOffset.MinValue;
        _cameraRetryAfter = DateTimeOffset.MinValue;
    }
}
