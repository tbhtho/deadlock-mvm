using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Native.InProcess;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Owns the small, live-proven Deadlock UI command surface. It deliberately
/// does not expose arbitrary cvars: every transition is typed, reversible and
/// restores Panorama after a partial or failed suppression attempt.
/// </summary>
public sealed class DeadlockUiController
{
    private const string HidePanoramaCommand = "r_drawpanorama false";
    private const string RestorePanoramaCommand = "r_drawpanorama true";

    private readonly ReplayController _replay;
    private readonly ILogService _log;
    private readonly object _gate = new();
    private DeadlockUiState _state = DeadlockUiState.Default;

    public DeadlockUiController(ReplayController replay, ILogService log)
    {
        _replay = replay;
        _log = log;
    }

    public DeadlockUiState State
    {
        get { lock (_gate) return _state; }
    }

    public bool Apply(DeadlockUiMode mode, bool replayActive)
    {
        if (!Enum.IsDefined(mode) || mode == DeadlockUiMode.DeathNoticesOnly)
            return Fail(DeadlockUiError.UnsupportedMode, "Death Notices Only is not independently controllable.");
        if (mode != DeadlockUiMode.DeadlockUi && !replayActive)
            return Fail(DeadlockUiError.ReplayUnavailable, "A live replay is required before hiding Deadlock UI.");
        if (!_replay.IsConnected)
            return Fail(DeadlockUiError.CommandChannelUnavailable, "Deadlock's command channel is unavailable.");

        try
        {
            _replay.SendRaw(mode == DeadlockUiMode.DeadlockUi
                ? RestorePanoramaCommand
                : HidePanoramaCommand);
            SetMode(mode, DeadlockUiError.None, string.Empty);
            _log.Info($"Deadlock UI mode applied: {mode}.");
            return true;
        }
        catch (Exception ex)
        {
            if (mode != DeadlockUiMode.DeadlockUi)
                TryRestoreAfterFailure();
            return Fail(DeadlockUiError.ApplyFailed, $"Could not apply {mode}: {ex.Message}");
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

    public bool Restore(bool force = false)
    {
        if (!force && State.Mode == DeadlockUiMode.DeadlockUi)
            return true;
        if (!_replay.IsConnected)
            return Fail(DeadlockUiError.CommandChannelUnavailable, "Deadlock UI restore is waiting for the command channel.");

        try
        {
            _replay.SendRaw(RestorePanoramaCommand);
            SetState(
                DeadlockUiMode.DeadlockUi,
                DeadlockUiMode.DeadlockUi,
                DeadlockUiError.None,
                string.Empty);
            _log.Info("Deadlock UI restored.");
            return true;
        }
        catch (Exception ex)
        {
            return Fail(DeadlockUiError.RestoreFailed, $"Could not restore Deadlock UI: {ex.Message}");
        }
    }

    private void TryRestoreAfterFailure()
    {
        try
        {
            if (_replay.IsConnected)
                _replay.SendRaw(RestorePanoramaCommand);
        }
        catch
        {
            // The native F9 fallback owns the final fail-closed attempt.
        }
    }

    private bool Fail(DeadlockUiError error, string detail)
    {
        var current = State;
        SetState(current.Mode, current.PreviousVisibleMode, error, detail);
        _log.Warn($"Deadlock UI: {detail}");
        return false;
    }

    private void SetMode(DeadlockUiMode mode, DeadlockUiError error, string detail)
    {
        lock (_gate)
        {
            var previousVisible = mode == DeadlockUiMode.CleanFootage
                ? NormalizeVisibleMode(_state.Mode == DeadlockUiMode.CleanFootage
                    ? _state.PreviousVisibleMode
                    : _state.Mode)
                : NormalizeVisibleMode(mode);
            _state = new DeadlockUiState(mode, previousVisible, Capabilities, error, detail);
        }
    }

    private void SetState(
        DeadlockUiMode mode,
        DeadlockUiMode previousVisibleMode,
        DeadlockUiError error,
        string detail)
    {
        lock (_gate)
            _state = new DeadlockUiState(
                mode,
                NormalizeVisibleMode(previousVisibleMode),
                Capabilities,
                error,
                detail);
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
