using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Native;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Composes the proven VConsole camera path (transform read, position write, POV,
/// free roam) with the native replay-camera backend (active FOV read/write, roaming
/// rotation write) into one <see cref="ICameraService"/>. Capabilities are computed
/// from what is actually working right now: native capabilities require an attached,
/// validated backend and confirmed demo playback, and follow modes restrict what is
/// writable (in-eye FOV comes from the pawn view path; follow-mode views come from
/// the observed target, so rotation writes only apply in free roam).
/// </summary>
public sealed class CompositeCameraService : ICameraService
{
    private readonly ICameraService _vconsole;
    private readonly NativeCameraBackend _native;
    private readonly IReplayPlaybackState _playback;
    private readonly ILogService? _log;

    private CameraShot? _savedShot;
    private CameraCapabilities? _lastCapabilities;

    public CompositeCameraService(
        ICameraService vconsole,
        NativeCameraBackend native,
        IReplayPlaybackState playback,
        ILogService? log = null)
    {
        _vconsole = vconsole;
        _native = native;
        _playback = playback;
        _log = log;

        _vconsole.SelectionChanged += (_, _) =>
        {
            RaiseCapabilitiesChangedIfNeeded();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        _native.StatusChanged += (_, _) => RaiseCapabilitiesChangedIfNeeded();
        _playback.StateChanged += (_, _) =>
        {
            RefreshNative();
            RaiseCapabilitiesChangedIfNeeded();
        };
    }

    public event EventHandler? SelectionChanged;

    public event EventHandler? CapabilitiesChanged;

    public SpectatorSelection Selection => _vconsole.Selection;

    public CameraShot? SavedShot => _savedShot;

    /// <summary>Native backend state for diagnostics (never shown in the editor UI).</summary>
    public NativeCameraStatus NativeStatus => _native.Status;

    public CameraCapabilities Capabilities => BuildCapabilities();

    public async Task<CameraState?> ReadStateAsync(CancellationToken cancellationToken = default)
        => Enrich(await _vconsole.ReadStateAsync(cancellationToken).ConfigureAwait(false));

    public async Task<CameraState?> ReadTransformAsync(CancellationToken cancellationToken = default)
        => Enrich(await _vconsole.ReadTransformAsync(cancellationToken).ConfigureAwait(false));

    public Task<CameraState?> EnterFreeRoamAsync(CancellationToken cancellationToken = default)
        => _vconsole.EnterFreeRoamAsync(cancellationToken);

    public void SelectPlayer(string playerOrSlot) => _vconsole.SelectPlayer(playerOrSlot);

    public void SelectNextPlayer() => _vconsole.SelectNextPlayer();

    public void SelectPrevPlayer() => _vconsole.SelectPrevPlayer();

    public void SelectInEye() => _vconsole.SelectInEye();

    public void SelectChase() => _vconsole.SelectChase();

    public void MoveRoamTarget(double x, double y, double z) => _vconsole.MoveRoamTarget(x, y, z);

    public Task<CameraState?> GoToPositionAsync(double x, double y, double z, CancellationToken cancellationToken = default)
        => _vconsole.GoToPositionAsync(x, y, z, cancellationToken);

    public void SetBaseFov(double fov) => _vconsole.SetBaseFov(fov);

    public Task<bool> SetActiveFovAsync(double fov, CancellationToken cancellationToken = default)
    {
        RefreshNative();
        if (!Capabilities.CanWriteActiveFov)
            return Task.FromResult(false);
        return Task.FromResult(_native.TrySetActiveFov(fov));
    }

    public Task<bool> SetCameraRotationAsync(double pitch, double yaw, double roll, CancellationToken cancellationToken = default)
    {
        RefreshNative();
        if (!Capabilities.CanWriteRotation)
            return Task.FromResult(false);
        return Task.FromResult(_native.TrySetCameraRotation(pitch, yaw, roll));
    }

    public async Task<CameraShot?> SaveCameraAsync(CancellationToken cancellationToken = default)
    {
        if (!Capabilities.CanSaveRestore)
            return null;

        var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (state?.ActiveTransform is not { } transform || state.ActiveFov is not { } fov)
            return null;

        _savedShot = new CameraShot(transform, fov);
        _log?.Info($"Camera: saved shot pos=({transform.X:0.###}, {transform.Y:0.###}, {transform.Z:0.###}) " +
                   $"ang=({transform.Pitch:0.###}, {transform.Yaw:0.###}, {transform.Roll:0.###}) fov={fov:0.###}");
        return _savedShot;
    }

    public async Task<bool> RestoreCameraAsync(CancellationToken cancellationToken = default)
    {
        if (_savedShot is not { } shot || !Capabilities.CanSaveRestore)
            return false;

        // Position rides the proven VConsole path (spec_goto also forces roaming,
        // which is the mode rotation writes apply in). The glide can slide on
        // world geometry and land off-target, so the landing is verified and
        // retried once from the closer position before giving up honestly.
        var landed = await LandOnShotAsync(shot, cancellationToken).ConfigureAwait(false);
        if (landed is null)
            return false;
        if (!PositionMatches(landed, shot.Transform))
        {
            _log?.Info($"Camera: restore glide landed off-shot ({FmtDelta(landed, shot.Transform)}), retrying once");
            landed = await LandOnShotAsync(shot, cancellationToken).ConfigureAwait(false);
            if (landed is null || !PositionMatches(landed, shot.Transform))
            {
                _log?.Warn($"Camera: restore could not reach the shot position ({FmtDelta(landed, shot.Transform)})");
                return false;
            }
        }

        var rotationOk = _native.TrySetCameraRotation(shot.Transform.Pitch, shot.Transform.Yaw, shot.Transform.Roll);
        var fovOk = _native.TrySetActiveFov(shot.Fov);
        if (!rotationOk || !fovOk)
            return false;

        // A restore only counts when the engine actually reports the shot back.
        // The rotation write takes a frame to propagate camera -> pawn -> render
        // cache -> getpos, so poll briefly for the engine to confirm the shot.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var verify = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            var t = verify?.ActiveTransform;
            var verified = t is { } v &&
                           Math.Abs(v.Pitch - shot.Transform.Pitch) < 0.5 &&
                           Math.Abs(NormalizeYawDelta(v.Yaw, shot.Transform.Yaw)) < 0.5 &&
                           verify!.ActiveFov is { } f && Math.Abs(f - shot.Fov) < 0.5;
            if (verified)
            {
                _log?.Info("Camera: shot restored and verified");
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        _log?.Warn("Camera: restore could not be verified against the engine report");
        return false;
    }

    private async Task<CameraTransform?> LandOnShotAsync(CameraShot shot, CancellationToken cancellationToken)
    {
        var settled = await _vconsole.GoToPositionAsync(
            shot.Transform.X, shot.Transform.Y, shot.Transform.Z, cancellationToken).ConfigureAwait(false);
        return settled?.ActiveTransform;
    }

    private static bool PositionMatches(CameraTransform actual, CameraTransform expected)
        => Math.Abs(actual.X - expected.X) <= 1.0 &&
           Math.Abs(actual.Y - expected.Y) <= 1.0 &&
           Math.Abs(actual.Z - expected.Z) <= 1.5;

    private static string FmtDelta(CameraTransform? actual, CameraTransform expected)
        => actual is { } a
            ? $"pos=({a.X:0.#}, {a.Y:0.#}, {a.Z:0.#}) vs shot ({expected.X:0.#}, {expected.Y:0.#}, {expected.Z:0.#})"
            : "no transform";

    private CameraState? Enrich(CameraState? state)
    {
        RefreshNative();
        if (state is null)
            return null;

        double? activeFov = null;
        if (Selection.Mode is SpecCameraMode.FreeRoam or SpecCameraMode.Chase)
            activeFov = _native.ReadActiveFov();

        return state with { ActiveFov = activeFov };
    }

    private void RefreshNative()
        => _native.Refresh(_playback.State is { Connected: true, ReplayName: not null });

    private CameraCapabilities BuildCapabilities()
    {
        var baseCaps = _vconsole.Capabilities;
        var status = _native.Status;
        var mode = Selection.Mode;

        var fovUsable = status.CanWriteActiveFov && mode is SpecCameraMode.FreeRoam or SpecCameraMode.Chase;
        var rotationUsable = status.CanWriteRotation && mode is SpecCameraMode.FreeRoam;

        return new CameraCapabilities
        {
            CanReadTransform = baseCaps.CanReadTransform,
            CanWritePosition = baseCaps.CanWritePosition,
            CanSelectPlayer = baseCaps.CanSelectPlayer,
            CanReadBaseFov = baseCaps.CanReadBaseFov,
            CanWriteBaseFov = baseCaps.CanWriteBaseFov,
            CanReadActiveFov = status.CanReadActiveFov && mode is SpecCameraMode.FreeRoam or SpecCameraMode.Chase,
            CanWriteActiveFov = fovUsable,
            CanWriteRotation = rotationUsable,
            CanSaveRestore = rotationUsable && fovUsable && baseCaps.CanWritePosition && baseCaps.CanReadTransform,
            Limitation = ComposeLimitation(status, mode),
        };
    }

    private static string ComposeLimitation(NativeCameraStatus status, SpecCameraMode mode)
    {
        if (!status.Attached)
            return "Active FOV and camera rotation need the native camera backend (not active).";
        if (mode == SpecCameraMode.InEye)
            return "Active FOV and rotation control are unavailable in In Eye.";
        if (mode == SpecCameraMode.Chase)
            return "Camera rotation control is unavailable while chasing a player.";
        if (mode != SpecCameraMode.FreeRoam)
            return "Camera control needs Free Roam.";
        return string.Empty;
    }

    private void RaiseCapabilitiesChangedIfNeeded()
    {
        var current = Capabilities;
        if (current == _lastCapabilities)
            return;
        _lastCapabilities = current;
        CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static double NormalizeYawDelta(double actual, double expected)
    {
        var delta = (actual - expected) % 360.0;
        if (delta > 180.0) delta -= 360.0;
        if (delta < -180.0) delta += 360.0;
        return delta;
    }
}
