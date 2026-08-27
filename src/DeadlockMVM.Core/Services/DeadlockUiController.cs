using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Native.InProcess;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Owns the fixed Deadlock replay-presentation command surface. It deliberately
/// does not expose arbitrary cvars: every transition is typed, has a known
/// inverse, and restores the complete presentation after a partial failure.
/// </summary>
public sealed class DeadlockUiController
{
    public static IReadOnlyList<string> ReplayPresentationCommands { get; } = Array.AsReadOnly(
    new[]
    {
        ReplayCommands.EnableReplayDevelopmentConVars,
        ReplayCommands.DisableFrameSpikeReports,
        "citadel_player_glow_disabled true",
        "citadel_trooper_glow_disabled true",
        "citadel_trooper_friendly_glow_disabled true",
        "citadel_trooper_outline_enabled false",
        "citadel_boss_glow_disabled true",
        "citadel_unit_status_allies_see_thru_walls false",
        "citadel_unit_status_enabled false",
        "citadel_healthbars_enabled false",
        "citadel_unit_status_max_total_bars 0",
        "r_citadel_glow_health_bars false",
        "citadel_hud_objective_health_enabled 0",
        "r_citadel_see_thru_walls_opacity 0",
        "citadel_unit_status_hide_names true",
        "citadel_unit_status_old_hide_names true",
        "citadel_camera_fade_viewed_near_opacity 1",
        "r_citadel_clip_sphere_min_opacity 1",
        "r_citadel_clip_sphere_distance_max 75",
        "r_drawpanorama false",
    });

    public static IReadOnlyList<string> DeadlockPresentationRestoreCommands { get; } = Array.AsReadOnly(
    new[]
    {
        "citadel_player_glow_disabled false",
        "citadel_trooper_glow_disabled false",
        "citadel_trooper_friendly_glow_disabled true",
        "citadel_trooper_outline_enabled false",
        "citadel_boss_glow_disabled false",
        "citadel_unit_status_allies_see_thru_walls true",
        "citadel_unit_status_enabled true",
        "citadel_healthbars_enabled true",
        "citadel_unit_status_max_total_bars 6",
        "r_citadel_glow_health_bars true",
        "citadel_hud_objective_health_enabled 2",
        "r_citadel_see_thru_walls_opacity 0.3",
        "citadel_unit_status_hide_names false",
        "citadel_unit_status_old_hide_names false",
        "citadel_camera_fade_viewed_near_opacity 0.4",
        "r_citadel_clip_sphere_min_opacity 0.4",
        "r_citadel_clip_sphere_distance_max 75",
        ReplayCommands.EnableFrameSpikeReports,
        "r_drawpanorama true",
        "sv_cheats 0",
    });

    /// <summary>
    /// Pause/resume and Tab can recreate Panorama without resetting the other
    /// movie-presentation cvars. Those frequent transitions only need this
    /// tiny idempotent batch; replaying the full profile on every state callback
    /// creates avoidable VConsole and render-thread pressure.
    /// </summary>
    public static IReadOnlyList<string> ReplayHudSuppressionCommands { get; } =
        Array.AsReadOnly(new[]
        {
            ReplayCommands.DisableFrameSpikeReports,
            "r_drawpanorama false",
        });

    private readonly ReplayController _replay;
    private readonly ILogService _log;
    private readonly object _gate = new();
    private readonly object _profileTransactionGate = new();
    private DeadlockUiState _state = DeadlockUiState.Default;
    private bool _profileRestorePending;
    private bool _profileTransactionInProgress;
    private ulong _profileAcknowledgementGeneration;
    private DeadlockUiMode _desiredMode = DeadlockUiMode.DeadlockUi;
    private DeadlockUiMode _desiredPreviousVisibleMode = DeadlockUiMode.DeadlockUi;

    public DeadlockUiController(ReplayController replay, ILogService log)
    {
        _replay = replay;
        _log = log;
    }

    public DeadlockUiState State
    {
        get { lock (_gate) return _state; }
    }

    public DeadlockUiProfileStatus ProfileStatus
    {
        get
        {
            lock (_gate)
                return new DeadlockUiProfileStatus(
                    _state,
                    _profileRestorePending,
                    _profileTransactionInProgress,
                    _profileAcknowledgementGeneration,
                    _desiredMode,
                    _desiredPreviousVisibleMode);
        }
    }

    internal bool ProfileRestorePending
    {
        get { lock (_gate) return _profileRestorePending; }
    }

    public bool Apply(
        DeadlockUiMode mode,
        bool replayActive,
        ulong recoveryGeneration = 0)
        => ApplyIfCurrent(mode, replayActive, static () => true, recoveryGeneration);

    /// <summary>
    /// Atomically validates owner intent under the same transaction gate that
    /// serializes the fixed VConsole profile. An emergency F9 either runs after
    /// this whole forward batch, or invalidates it before the first command.
    /// </summary>
    public bool ApplyIfCurrent(
        DeadlockUiMode mode,
        bool replayActive,
        Func<bool> stillCurrent,
        ulong recoveryGeneration = 0)
    {
        ArgumentNullException.ThrowIfNull(stillCurrent);
        if (!Enum.IsDefined(mode) || mode == DeadlockUiMode.DeathNoticesOnly)
            return Fail(DeadlockUiError.UnsupportedMode, "Death Notices Only is not independently controllable.");
        if (mode != DeadlockUiMode.DeadlockUi && !replayActive)
            return Fail(DeadlockUiError.ReplayUnavailable, "A live replay is required before hiding Deadlock UI.");
        lock (_profileTransactionGate)
        {
            if (!stillCurrent())
                return false;
            if (mode == DeadlockUiMode.DeadlockUi)
                return RestoreLocked(force: true, recoveryGeneration);
            if (!_replay.IsConnected)
                return Fail(DeadlockUiError.CommandChannelUnavailable, "Deadlock's command channel is unavailable.");

            SetDesiredForwardMode(mode);
            BeginProfileApply();
            try
            {
                SendProfile(ReplayPresentationCommands);
                CompleteProfileApply(mode, recoveryGeneration);
                _log.Info(
                    $"Deadlock UI mode applied: {mode} " +
                    $"(full profile, {ReplayPresentationCommands.Count} commands, " +
                    $"ack={recoveryGeneration}).");
                return true;
            }
            catch (Exception ex)
            {
                TryRestoreAfterFailureLocked();
                return Fail(DeadlockUiError.ApplyFailed, $"Could not apply {mode}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Replays the active suppression command without changing presentation
    /// mode. Replay transport and editor-menu transitions can make the engine
    /// recreate Panorama even though SMVM still owns the movie interface.
    /// </summary>
    public bool ReassertSuppression(bool replayActive) =>
        ReassertSuppressionIfCurrent(replayActive, static () => true);

    /// <summary>
    /// Reasserts only the inexpensive runtime suppression commands while the
    /// caller's process/replay lease remains current. Pause, resume, Tab, and
    /// seek landing must never reinstall the complete render profile.
    /// </summary>
    public bool ReassertSuppressionIfCurrent(bool replayActive, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(stillCurrent);
        var profile = ProfileStatus;
        if (profile.DesiredMode == DeadlockUiMode.DeadlockUi)
            return profile.Ui.Mode == DeadlockUiMode.DeadlockUi && !profile.RestorePending;
        if (!replayActive)
            return Fail(DeadlockUiError.ReplayUnavailable, "A live replay is required before hiding Deadlock UI.");

        lock (_profileTransactionGate)
        {
            if (!stillCurrent())
                return false;
            profile = ProfileStatus;
            if (profile.DesiredMode == DeadlockUiMode.DeadlockUi)
                return profile.Ui.Mode == DeadlockUiMode.DeadlockUi && !profile.RestorePending;
            // This two-command path is only safe after the complete forward
            // presentation profile has been acknowledged. If a prior apply
            // failed and restored Deadlock's physical state, a thin reassert
            // must not retire that recovery debt or claim the desired mode.
            if (profile.Ui.Mode != profile.DesiredMode ||
                profile.TransactionInProgress ||
                profile.ShouldRetryForwardProfile)
                return false;
            if (!_replay.IsConnected)
                return Fail(DeadlockUiError.CommandChannelUnavailable, "Deadlock's command channel is unavailable.");

            SetDesiredForwardMode(profile.DesiredMode);
            BeginProfileApply();
            try
            {
                SendProfile(ReplayHudSuppressionCommands);
                CompleteProfileApply(profile.DesiredMode, profile.AcknowledgementGeneration);
                _log.Info(
                    $"Deadlock HUD suppression reasserted " +
                    $"({ReplayHudSuppressionCommands.Count} commands).");
                return true;
            }
            catch (Exception ex)
            {
                TryRestoreAfterFailureLocked();
                return Fail(DeadlockUiError.ApplyFailed, $"Could not reassert Deadlock HUD suppression: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Leaves Clean Footage without guessing which visible interface the editor
    /// was using before it was hidden.
    /// </summary>
    public bool RestorePreviousVisibleMode(bool replayActive)
    {
        var state = State;
        if (state.Mode != DeadlockUiMode.CleanFootage)
            return true;
        return Apply(NormalizeVisibleMode(state.PreviousVisibleMode), replayActive);
    }

    public bool Restore(bool force = false, ulong recoveryGeneration = 0)
    {
        lock (_profileTransactionGate)
            return RestoreLocked(force, recoveryGeneration);
    }

    public bool RetryPendingRestore()
    {
        lock (_profileTransactionGate)
            return RestoreLocked(force: false, preserveDesired: true);
    }

    /// <summary>
    /// Clean Footage is a rolling-recording state, never a startup preference.
    /// A true replay/process boundary returns to the visible mode that preceded
    /// it. SMVM UI uses the same physical recording profile, while Deadlock UI
    /// requires the full inverse transaction.
    /// </summary>
    public DeadlockUiMode NormalizeTransientCleanForNewReplay()
    {
        lock (_profileTransactionGate)
        {
            var profile = ProfileStatus;
            if (profile.DesiredMode != DeadlockUiMode.CleanFootage)
                return profile.DesiredMode;

            var target = NormalizeVisibleMode(profile.DesiredPreviousVisibleMode);
            if (target == DeadlockUiMode.DeadlockUi)
            {
                _ = RestoreLocked(force: true);
                return DeadlockUiMode.DeadlockUi;
            }

            lock (_gate)
            {
                _desiredMode = target;
                _desiredPreviousVisibleMode = target;
                if (_state.Mode == DeadlockUiMode.CleanFootage)
                {
                    _state = _state with
                    {
                        Mode = target,
                        PreviousVisibleMode = target,
                    };
                }
            }
            return target;
        }
    }

    /// <summary>
    /// Restores the physical profile after the native connection lease changes,
    /// while retaining a visible error marker so reconnect recovery does not
    /// mistake this safety rollback for an intentional owner selection.
    /// </summary>
    public bool RestoreAfterConnectionLeaseLoss()
    {
        lock (_profileTransactionGate)
        {
            if (!RestoreLocked(force: true, preserveDesired: true))
                return false;
            SetState(
                DeadlockUiMode.DeadlockUi,
                DeadlockUiMode.DeadlockUi,
                DeadlockUiError.CommandChannelUnavailable,
                "The native presentation connection changed; SMVM safely restored Deadlock UI and will reassert after reconnect.");
            return true;
        }
    }

    private bool RestoreLocked(
        bool force,
        ulong recoveryGeneration = 0,
        bool preserveDesired = false)
    {
        if (!preserveDesired)
            ClearDesiredMode();
        var profile = ProfileStatus;
        if (recoveryGeneration != 0 &&
            profile.AcknowledgementGeneration == recoveryGeneration &&
            !profile.RequiresRestore &&
            !profile.TransactionInProgress)
        {
            return true;
        }
        if (!force && !profile.RequiresRestore)
            return true;
        BeginProfileRestore();
        if (!_replay.IsConnected)
        {
            MarkProfileRestoreFailed();
            return Fail(DeadlockUiError.CommandChannelUnavailable, "Deadlock UI restore is waiting for the command channel.");
        }

        try
        {
            SendProfile(DeadlockPresentationRestoreCommands);
            CompleteProfileRestore(recoveryGeneration);
            _log.Info("Deadlock UI restored.");
            return true;
        }
        catch (Exception ex)
        {
            MarkProfileRestoreFailed();
            return Fail(DeadlockUiError.RestoreFailed, $"Could not restore Deadlock UI: {ex.Message}");
        }
    }

    private void TryRestoreAfterFailureLocked()
    {
        BeginProfileRestore();
        try
        {
            if (_replay.IsConnected)
            {
                SendProfile(DeadlockPresentationRestoreCommands);
                CompleteProfileRestore();
            }
            else
            {
                MarkProfileRestoreFailed();
            }
        }
        catch
        {
            MarkProfileRestoreFailed();
            // The native F9 fallback owns the final fail-closed attempt.
        }
    }

    private void SendProfile(IReadOnlyList<string> commands)
    {
        foreach (var command in commands)
            _replay.SendRaw(command);
    }

    private void BeginProfileApply()
    {
        lock (_gate)
        {
            _profileRestorePending = true;
            _profileTransactionInProgress = true;
        }
    }

    private void CompleteProfileApply(DeadlockUiMode mode, ulong recoveryGeneration)
    {
        lock (_gate)
        {
            _state = new DeadlockUiState(
                mode,
                mode == DeadlockUiMode.CleanFootage
                    ? _desiredPreviousVisibleMode
                    : NormalizeVisibleMode(mode),
                Capabilities,
                DeadlockUiError.None,
                string.Empty);
            _profileTransactionInProgress = false;
            if (recoveryGeneration != 0)
                _profileAcknowledgementGeneration = recoveryGeneration;
        }
    }

    private void BeginProfileRestore()
    {
        lock (_gate)
        {
            _state = new DeadlockUiState(
                DeadlockUiMode.DeadlockUi,
                DeadlockUiMode.DeadlockUi,
                Capabilities,
                DeadlockUiError.None,
                string.Empty);
            _profileRestorePending = true;
            _profileTransactionInProgress = true;
        }
    }

    private void MarkProfileRestoreFailed()
    {
        lock (_gate)
        {
            _profileRestorePending = true;
            _profileTransactionInProgress = false;
        }
    }

    private void CompleteProfileRestore(ulong recoveryGeneration = 0)
    {
        lock (_gate)
        {
            _state = new DeadlockUiState(
                DeadlockUiMode.DeadlockUi,
                DeadlockUiMode.DeadlockUi,
                Capabilities,
                DeadlockUiError.None,
                string.Empty);
            _profileRestorePending = false;
            _profileTransactionInProgress = false;
            if (recoveryGeneration != 0)
                _profileAcknowledgementGeneration = recoveryGeneration;
        }
    }

    private void SetDesiredForwardMode(DeadlockUiMode mode)
    {
        lock (_gate)
        {
            if (_desiredMode != mode)
            {
                _desiredPreviousVisibleMode = mode == DeadlockUiMode.CleanFootage
                    ? NormalizeVisibleMode(
                        _state.Mode == DeadlockUiMode.CleanFootage
                            ? _state.PreviousVisibleMode
                            : _state.Mode)
                    : NormalizeVisibleMode(mode);
            }
            _desiredMode = mode;
        }
    }

    private void ClearDesiredMode()
    {
        lock (_gate)
        {
            _desiredMode = DeadlockUiMode.DeadlockUi;
            _desiredPreviousVisibleMode = DeadlockUiMode.DeadlockUi;
        }
    }

    private bool Fail(DeadlockUiError error, string detail)
    {
        var current = State;
        SetState(current.Mode, current.PreviousVisibleMode, error, detail);
        _log.Warn($"Deadlock UI: {detail}");
        return false;
    }

    private void SetModeLocked(DeadlockUiMode mode, DeadlockUiError error, string detail)
    {
        var previousVisible = mode == DeadlockUiMode.CleanFootage
            ? NormalizeVisibleMode(_state.Mode == DeadlockUiMode.CleanFootage
                ? _state.PreviousVisibleMode
                : _state.Mode)
            : NormalizeVisibleMode(mode);
        _state = new DeadlockUiState(mode, previousVisible, Capabilities, error, detail);
    }

    private void SetState(
        DeadlockUiMode mode,
        DeadlockUiMode previousVisibleMode,
        DeadlockUiError error,
        string detail)
    {
        lock (_gate)
        {
            _state = new DeadlockUiState(
                mode,
                NormalizeVisibleMode(previousVisibleMode),
                Capabilities,
                error,
                detail);
        }
    }

    private static DeadlockUiMode NormalizeVisibleMode(DeadlockUiMode mode) =>
        mode is DeadlockUiMode.DeadlockUi or DeadlockUiMode.SmvmReplayUi
            ? mode
            : DeadlockUiMode.DeadlockUi;

    public static DeadlockUiCapabilities Capabilities =>
        DeadlockUiCapabilities.HidePanorama |
        DeadlockUiCapabilities.RestorePanorama |
        DeadlockUiCapabilities.SmvmReplayUi |
        DeadlockUiCapabilities.CleanFootage;
}

public sealed record DeadlockUiProfileStatus(
    DeadlockUiState Ui,
    bool RestorePending,
    bool TransactionInProgress,
    ulong AcknowledgementGeneration = 0,
    DeadlockUiMode DesiredMode = DeadlockUiMode.DeadlockUi,
    DeadlockUiMode DesiredPreviousVisibleMode = DeadlockUiMode.DeadlockUi)
{
    public bool RequiresRestore =>
        Ui.Mode != DeadlockUiMode.DeadlockUi || RestorePending;

    public bool ShouldRetryRestore =>
        Ui.Mode == DeadlockUiMode.DeadlockUi &&
        RestorePending &&
        !TransactionInProgress;

    public bool ShouldRetryForwardProfile =>
        (DesiredMode is DeadlockUiMode.SmvmReplayUi or DeadlockUiMode.CleanFootage) &&
        Ui.Mode == DeadlockUiMode.DeadlockUi &&
        !RestorePending &&
        !TransactionInProgress;
}

public sealed record DeadlockUiState(
    DeadlockUiMode Mode,
    DeadlockUiMode PreviousVisibleMode,
    DeadlockUiCapabilities Capabilities,
    DeadlockUiError Error,
    string Detail)
{
    public static DeadlockUiState Default { get; } = new(
        DeadlockUiMode.DeadlockUi,
        DeadlockUiMode.DeadlockUi,
        DeadlockUiController.Capabilities,
        DeadlockUiError.None,
        string.Empty);
}
