using DeadlockMVM.Core.Contracts;

namespace DeadlockMVM.Core.Native;

/// <summary>Snapshot of the native backend's attach state and resolved capabilities.</summary>
public sealed record NativeCameraStatus(
    bool Attached,
    bool ReplayActive,
    bool CanReadActiveFov,
    bool CanWriteActiveFov,
    bool CanWriteRotation,
    string? Fingerprint,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Thin native replay-camera backend: supplies only the two camera primitives the
/// VConsole surface provably lacks — active spectator FOV read/write and roaming
/// camera rotation write — via external process memory against signature-resolved
/// addresses (see <c>tools/research/native_backend.md</c>). Local replay moviemaking
/// only: attach requires the game process, a fingerprint-matching client.dll, and
/// validated pointer chains; writes additionally require confirmed demo playback.
/// Any failure disables the affected capability instead of risking a wrong write.
///
/// Exact native position write was INVESTIGATED and is deliberately absent: live
/// tests (2026-08-22) showed every reachable field is re-derived per frame during
/// playback — the camera origin (cam+0x38) is re-anchored from the pawn, and the
/// pawn scene node's local (node+0xE4) / abs (node+0xC8) origins revert within a
/// frame. The true authority is the entity's move-state origin, which only the
/// engine's Teleport path (spec_goto) writes; do not re-add without live proof.
/// </summary>
public sealed class NativeCameraBackend : IDisposable
{
    /// <summary>FOV policy range. Direct value writes bypass engine clamping; the
    /// bounds mirror what the engine itself tolerates on this camera (chase clamp
    /// is [40,170]) with margin below for cinematic narrow lenses.</summary>
    public const double MinFov = 5;
    public const double MaxFov = 170;

    private const int ConVarRefDataOffset = 0x08;      // ref slot + 8 -> ConVar data block (Q)
    private const int ConVarDataMinPtrOffset = 0x10;   // Q + 0x10 -> float* min (nullable)
    private const int ConVarDataMaxPtrOffset = 0x18;   // Q + 0x18 -> float* max (nullable)
    private const int ConVarDataValueOffset = 0x58;    // Q + 0x58 -> live float value

    private const int ManagerCurrentCameraOffset = 0x28;
    private const int CameraAnglesOffset = 0x44;       // pitch/yaw/roll
    private const int CameraFovOffset = 0x50;          // m_flFOV — the rendered FOV
    private const int CameraAnglesMirrorOffset = 0xBC;
    private const int CameraAnglesQuantizedOffset = 0xD4; // GetAngles returns these; roll slot zeroed

    private readonly object _sync = new();
    private readonly INativeHost _host;
    private readonly ILogService? _log;
    private readonly Dictionary<ModuleFingerprint, ResolvedCameraAddresses> _resolutionCache = new();

    private IProcessMemory? _memory;
    private bool _attached;
    private bool _replayActive;
    private bool _canUseFov;
    private bool _canUseCamera;
    private ulong _clientBase;
    private long _clientSize;
    private ulong _fovRefAddress;
    private ulong _managerAddress;
    private string? _fingerprintText;
    private IReadOnlyList<string> _diagnostics = Array.Empty<string>();

    public NativeCameraBackend(ILogService? log = null)
        : this(new Win32NativeHost(), log)
    {
    }

    internal NativeCameraBackend(INativeHost host, ILogService? log = null)
    {
        _host = host;
        _log = log;
    }

    public event EventHandler? StatusChanged;

    /// <summary>Whether the backend is attached and at least one capability resolved.</summary>
    public bool Attached
    {
        get { lock (_sync) return _attached; }
    }

    public NativeCameraStatus Status
    {
        get
        {
            lock (_sync)
            {
                return new NativeCameraStatus(
                    _attached,
                    _replayActive,
                    _attached && _canUseCamera,
                    _attached && _canUseFov && _canUseCamera,
                    _attached && _canUseCamera,
                    _fingerprintText,
                    _diagnostics);
            }
        }
    }

    /// <summary>
    /// Drives the attach/detach lifecycle. Call periodically (the camera poll loop is
    /// the natural driver) with whether demo playback is currently confirmed.
    /// </summary>
    public void Refresh(bool replayActive)
    {
        lock (_sync)
        {
            _replayActive = replayActive;
            if (!replayActive)
            {
                if (_attached)
                    DetachLocked("demo playback no longer confirmed");
                return;
            }

            if (_attached)
            {
                // Cheap liveness: the camera object must still resolve.
                if (!TryReadCameraAddressLocked(out _))
                    DetachLocked("camera chain no longer readable");
                return;
            }

            TryAttachLocked();
        }
    }

    /// <summary>The active camera's rendered FOV (cam+0x50), or null when unavailable.</summary>
    public double? ReadActiveFov()
    {
        lock (_sync)
        {
            if (!_attached || !_canUseCamera)
                return null;
            if (!TryReadCameraAddressLocked(out var camera) ||
                !_memory!.TryReadF32(camera + CameraFovOffset, out var fov) ||
                !IsPlausibleFov(fov))
            {
                DetachLocked("active FOV read failed");
                return null;
            }

            return fov;
        }
    }

    /// <summary>
    /// Writes the spectator-FOV ConVar value (applied per frame by the game's own
    /// spectator builder) and the active camera's FOV field directly (covers
    /// targetless free-roam where the builder does not re-pull the cvar).
    /// </summary>
    public bool TrySetActiveFov(double fov)
    {
        lock (_sync)
        {
            if (!_attached || !_replayActive || !_canUseFov || !_canUseCamera)
                return false;
            if (!double.IsFinite(fov) || fov < MinFov || fov > MaxFov)
                return false;

            var value = (float)fov;
            var memory = _memory;
            if (memory is null)
            {
                DetachLocked("FOV write lost process memory");
                return false;
            }
            if (!TryReadConVarDataLocked(out var conVarData) || !TryReadCameraAddressLocked(out var camera))
            {
                DetachLocked("FOV write lost the pointer chains");
                return false;
            }

            if (!memory.TryWriteF32(conVarData + ConVarDataValueOffset, value) ||
                !memory.TryWriteF32(camera + CameraFovOffset, value))
            {
                DetachLocked("FOV write failed");
                return false;
            }

            _log?.Info($"NativeCamera: set active FOV to {value:0.###}");
            return true;
        }
    }

    /// <summary>
    /// Writes roaming-camera rotation, mirroring the game's own camera writer
    /// (angles + mirror + 1/32-degree quantized outputs; roll output slot zeroed
    /// exactly like the game does). Verified for free roam; modes with a tracked
    /// spectator target may fight the write (auto-target-view).
    /// </summary>
    public bool TrySetCameraRotation(double pitch, double yaw, double roll)
    {
        lock (_sync)
        {
            if (!_attached || !_replayActive || !_canUseCamera)
                return false;
            if (!double.IsFinite(pitch) || !double.IsFinite(yaw) || !double.IsFinite(roll))
                return false;

            var p = (float)Math.Clamp(pitch, -89.0, 89.0);
            var y = (float)NormalizeYaw(yaw);
            var r = (float)roll;
            var memory = _memory;
            if (memory is null)
            {
                DetachLocked("rotation write lost process memory");
                return false;
            }

            if (!TryReadCameraAddressLocked(out var camera))
            {
                DetachLocked("rotation write lost the camera chain");
                return false;
            }

            var ok = memory.TryWrite3F32(camera + CameraAnglesOffset, p, y, r) &&
                     memory.TryWrite3F32(camera + CameraAnglesMirrorOffset, p, y, r) &&
                     memory.TryWrite3F32(camera + CameraAnglesQuantizedOffset, Quantize(p), Quantize(y), 0f);
            if (!ok)
            {
                DetachLocked("rotation write failed");
                return false;
            }

            _log?.Info($"NativeCamera: set camera rotation to pitch={p:0.###} yaw={y:0.###} roll={r:0.###}");
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _memory?.Dispose();
            _memory = null;
            _attached = false;
        }
    }

    /// <summary>Dev/diagnostic read of a camera object field (used by LiveVerify probes).</summary>
    internal bool TryReadCameraU32(int offset, out uint value)
    {
        lock (_sync)
        {
            value = 0;
            if (!_attached || !_canUseCamera || !TryReadCameraAddressLocked(out var camera))
                return false;
            Span<byte> buffer = stackalloc byte[4];
            if (!_memory!.TryRead(camera + (ulong)offset, buffer))
                return false;
            value = BitConverter.ToUInt32(buffer);
            return true;
        }
    }

    /// <summary>Dev/diagnostic float read of a camera object field (LiveVerify probes).</summary>
    internal bool TryReadCameraF32(int offset, out float value)
    {
        var ok = TryReadCameraU32(offset, out var bits);
        value = ok ? BitConverter.UInt32BitsToSingle(bits) : 0;
        return ok;
    }

    // ---- attach / validate -------------------------------------------------

    private void TryAttachLocked()
    {
        var diagnostics = new List<string>();
        _canUseFov = false;
        _canUseCamera = false;

        var processId = _host.FindGameProcessId();
        if (processId is null)
            return; // game not running: stay silently detached, Refresh will retry
        diagnostics.Add($"process pid={processId.Value}");

        if (!_host.TryFindClientModule(processId.Value, out var moduleBase, out var moduleSize, out var modulePath))
        {
            _diagnostics = With(diagnostics, "client.dll module not found in process");
            return;
        }
        diagnostics.Add($"module base=0x{moduleBase:X} size=0x{moduleSize:X}");
        diagnostics.Add($"path {modulePath}");

        var memory = _host.OpenProcess(processId.Value);
        if (memory is null)
        {
            _diagnostics = With(diagnostics, "OpenProcess failed (access denied or process exited)");
            return;
        }

        PeImage fileImage;
        try
        {
            fileImage = PeImage.Load(File.ReadAllBytes(modulePath));
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            memory.Dispose();
            _diagnostics = With(diagnostics, $"client.dll file unreadable: {ex.Message}");
            return;
        }

        // The live module header must match the file we resolved against — never
        // trust addresses against a build that changed underneath a running game.
        var header = new byte[4096];
        ModuleFingerprint liveFingerprint;
        try
        {
            if (!memory.TryRead(moduleBase, header))
                throw new InvalidDataException("module header read failed");
            liveFingerprint = PeImage.ReadFingerprint(header, fileImage.Fingerprint.FileLength);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidDataException)
        {
            memory.Dispose();
            _diagnostics = With(diagnostics, $"live module header unreadable: {ex.Message}");
            return;
        }

        _fingerprintText = $"TDS=0x{liveFingerprint.TimeDateStamp:X8} checksum=0x{liveFingerprint.CheckSum:X8} " +
                           $"size=0x{liveFingerprint.SizeOfImage:X}";
        if (!liveFingerprint.Matches(fileImage.Fingerprint))
        {
            memory.Dispose();
            _diagnostics = With(diagnostics,
                $"fingerprint mismatch: live={_fingerprintText} file=TDS=0x{fileImage.Fingerprint.TimeDateStamp:X8} " +
                $"checksum=0x{fileImage.Fingerprint.CheckSum:X8} size=0x{fileImage.Fingerprint.SizeOfImage:X}");
            _log?.Warn("NativeCamera: build fingerprint mismatch — signatures not trusted");
            return;
        }
        diagnostics.Add($"fingerprint {_fingerprintText} MATCH");

        if (!_resolutionCache.TryGetValue(fileImage.Fingerprint, out var resolved))
        {
            resolved = NativeCameraResolver.Resolve(fileImage);
            _resolutionCache[fileImage.Fingerprint] = resolved;
        }

        foreach (var item in resolved.Items)
        {
            diagnostics.Add(item.Success
                ? $"sig {item.Name}: OK hits={item.MatchCount} rva=0x{item.Rva:X}"
                : $"sig {item.Name}: FAIL hits={item.MatchCount} {item.Error}");
        }

        _clientBase = moduleBase;
        _clientSize = moduleSize;
        _fovRefAddress = resolved.SpectatorFovRefRva != 0 ? moduleBase + (ulong)resolved.SpectatorFovRefRva : 0;
        _managerAddress = resolved.CameraManagerRva != 0 ? moduleBase + (ulong)resolved.CameraManagerRva : 0;

        _memory?.Dispose();
        _memory = memory;

        // Validate the ConVar chain: Q must be registered, min/max sane, value plausible.
        if (_fovRefAddress != 0 && TryReadConVarDataLocked(out var conVarData))
        {
            var minOk = memory.TryReadU64(conVarData + ConVarDataMinPtrOffset, out var minPtr) && minPtr != 0;
            var maxOk = memory.TryReadU64(conVarData + ConVarDataMaxPtrOffset, out var maxPtr) && maxPtr != 0;
            if (minOk && maxOk &&
                memory.TryReadF32(minPtr, out var min) && memory.TryReadF32(maxPtr, out var max) &&
                IsPlausibleFov(min) && IsPlausibleFov(max) && min < max &&
                memory.TryReadF32(conVarData + ConVarDataValueOffset, out var value) && IsPlausibleFov(value))
            {
                _canUseFov = true;
                diagnostics.Add($"convar data Q=0x{conVarData:X} value={value:0.###} range=[{min:0.#},{max:0.#}]");
            }
            else
            {
                diagnostics.Add("convar data validation failed (min/max/value implausible)");
            }
        }
        else if (_fovRefAddress != 0)
        {
            diagnostics.Add("convar data pointer is null (cvar not registered yet)");
        }

        // Validate the camera chain: current camera non-null, vtable inside the
        // module, FOV field plausible.
        if (_managerAddress != 0 && TryReadCameraAddressLocked(out var camera))
        {
            if (memory.TryReadU64(camera, out var vtable) &&
                vtable >= moduleBase && vtable < moduleBase + (ulong)moduleSize &&
                memory.TryReadF32(camera + CameraFovOffset, out var cameraFov) && IsPlausibleFov(cameraFov))
            {
                _canUseCamera = true;
                diagnostics.Add($"camera 0x{camera:X} vtable ok fov={cameraFov:0.###}");
            }
            else
            {
                diagnostics.Add("camera validation failed (vtable outside module or implausible FOV)");
            }
        }
        else if (_managerAddress != 0)
        {
            diagnostics.Add("no current camera (not in a replay/level yet)");
        }

        // The FOV lever is useless without the camera (the write targets both the
        // ConVar value and cam+0x50), so a camera-chain failure takes FOV with it.
        _canUseFov = _canUseFov && _canUseCamera;

        _diagnostics = diagnostics;
        if (!_canUseFov && !_canUseCamera)
            return; // stay detached; diagnostics explain why

        _attached = true;
        _log?.Info($"NativeCamera: attached (fov={_canUseFov}, camera={_canUseCamera}) — {_fingerprintText}");
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DetachLocked(string reason)
    {
        _attached = false;
        _canUseFov = false;
        _canUseCamera = false;
        _memory?.Dispose();
        _memory = null;
        _diagnostics = With(_diagnostics, $"detached: {reason}");
        _log?.Info($"NativeCamera: {reason}");
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryReadConVarDataLocked(out ulong conVarData)
    {
        conVarData = 0;
        return _memory is not null && _fovRefAddress != 0 &&
               _memory.TryReadU64(_fovRefAddress + ConVarRefDataOffset, out conVarData) && conVarData != 0;
    }

    private bool TryReadCameraAddressLocked(out ulong camera)
    {
        camera = 0;
        return _memory is not null && _managerAddress != 0 &&
               _memory.TryReadU64(_managerAddress + ManagerCurrentCameraOffset, out camera) && camera != 0;
    }

    private static bool IsPlausibleFov(float fov) => float.IsFinite(fov) && fov is >= 1 and <= 179;

    private static double NormalizeYaw(double yaw)
    {
        yaw %= 360.0;
        if (yaw >= 180.0)
            yaw -= 360.0;
        else if (yaw < -180.0)
            yaw += 360.0;
        return yaw;
    }

    /// <summary>The game's 1/32-degree output quantization (floor(f*32)/32).</summary>
    private static float Quantize(float degrees) => (float)(Math.Floor(degrees * 32.0) / 32.0);

    private static IReadOnlyList<string> With(IReadOnlyList<string> diagnostics, string line)
    {
        var copy = new List<string>(diagnostics) { line };
        return copy;
    }
}
