using System.Buffers.Binary;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;

namespace DeadlockMVM.Core.Native.InProcess;

internal enum InProcessMessageType : ushort
{
    Hello = 1,
    Heartbeat = 2,
    EnableOverride = 3,
    DisableOverride = 4,
    SetCameraSample = 5,
    GetStatus = 6,
    Shutdown = 7,
    SetCampath = 8,
    ClearCampath = 9,
    PrepareCameraObservation = 10,
    UpdateSmvmSnapshot = 11,
    SetEditorCampath = 12,
    ClearEditorCampath = 13,
    SetRollOverride = 14,
    EnableManualCamera = 15,
    DisableManualCamera = 16,
    SetCampathDocuments = 17,
    SetLookLut = 18,
    Status = 100,
}

public enum InProcessBackendState : uint
{
    Unavailable = 0,
    Loading = 1,
    Connected = 2,
    Ready = 3,
    Failed = 4,
}

public enum InProcessErrorCode : uint
{
    None = 0,
    WrongProcess = 1,
    ReplayLaunchRequired = 2,
    ClientModuleMissing = 3,
    SignatureMissing = 4,
    SignatureAmbiguous = 5,
    CameraUnavailable = 6,
    HookTargetMismatch = 7,
    HookInstallFailed = 8,
    ProtocolError = 9,
    ReplayGateClosed = 10,
    InvalidSample = 11,
    ObserverNotRoaming = 12,
    HeartbeatStale = 13,
    HookRuntimeInvalid = 14,
    ReplayClockUnavailable = 15,
}

public enum SmvmRendererBackend : uint
{
    None = 0,
    D3D11 = 1,
    Unsupported = 2,
}

public enum SmvmRendererError : uint
{
    None = 0,
    RendererNotLoaded = 1,
    UnsupportedRenderer = 2,
    SwapchainProbeFailed = 3,
    HookInstallFailed = 4,
    PresentNotObserved = 5,
    DeviceUnavailable = 6,
    ResourceCreationFailed = 7,
    WindowHookFailed = 8,
    DeviceReset = 9,
}

[Flags]
public enum SmvmOverlayFlags : uint
{
    None = 0,
    HookInstalled = 1 << 0,
    PresentObserved = 1 << 1,
    Ready = 1 << 2,
    MenuOpen = 1 << 3,
    CleanView = 1 << 4,
    ManualPointerActive = 1 << 5,
    ManualMouseObserved = 1 << 6,
    ManualPointerRequested = 1 << 7,
    KeyboardReady = 1 << 8,
    RelativeMouseReady = 1 << 9,
    RawInputReady = 1 << 10,
    CursorReady = 1 << 11,
    ForegroundReady = 1 << 12,
    WindowProcedureReady = 1 << 13,
    EngineInputReady = 1 << 14,
    FallbackMouseObserved = 1 << 15,
    CreepHealthbarHookInstalled = 1 << 16,
    CreepHealthbarSuppressionObserved = 1 << 17,
    TowerOutlineHooksInstalled = 1 << 18,
    TowerOutlineSuppressionObserved = 1 << 19,
    TowerFadeOverrideInstalled = 1 << 20,
    TowerFadeOverrideEnforced = 1 << 21,
    CreepHealthbarHookRetrying = 1 << 22,
    TowerOutlineHooksRetrying = 1 << 23,
    TowerFadeOverrideRetrying = 1 << 24,
    MovieRecordingActive = 1 << 25,
    WorldDepthAvailable = 1 << 26,
    WorldDepthFailed = 1 << 27,
    MovieAviAvailable = 1 << 28,
    WorldDepthIncomplete = 1 << 29,
    MovieFrameReady = 1 << 30,
}

[Flags]
public enum SmvmSnapshotFlags : uint
{
    None = 0,
    ReplayActive = 1 << 0,
    PauseKnown = 1 << 1,
    Paused = 1 << 2,
    CameraReadable = 1 << 3,
    FovWritable = 1 << 4,
    RollWritable = 1 << 5,
    CampathPlaying = 1 << 6,
    CameraOwned = 1 << 7,
    EditorPath = 1 << 8,
    InternalEnabled = 1 << 9,
    ShowToolbar = 1 << 10,
    ShowPath = 1 << 11,
    ShowCameras = 1 << 12,
    ShowLabels = 1 << 13,
    FovInverted = 1 << 14,
    ManualCameraRequested = 1 << 15,
    ManualCameraActive = 1 << 16,
    InputTakeover = 1 << 17,
    InvertY = 1 << 18,
    ShowMinimalPill = 1 << 19,
    Notifications = 1 << 20,
    HidePathWhilePlaying = 1 << 21,
    CampathUnsaved = 1 << 22,
    CampathRecoveryAvailable = 1 << 23,
    RestoreWorkspace = 1 << 24,
    CaptureDiagnostics = 1 << 25,
    ShowStatusHud = 1 << 26,
    ReplaySeekInProgress = 1 << 27,
    RecordingProfileRestorePending = 1 << 28,
    RecordingProfileTransactionInProgress = 1 << 29,
}

[Flags]
public enum MovieRecordingFlags : uint
{
    None = 0,
    DisablePostProcessing = 1 << 0,
    MuteDialogue = 1 << 1,
    WorldDepth = 1 << 3,
    Active = 1 << 4,
    Armed = 1 << 5,
    Finalizing = 1 << 6,
}

public enum MovieRecordingOption
{
    DisablePostProcessing = 0,
    MuteDialogue = 1,
}

[Flags]
public enum MovieToolFlags : uint
{
    None = 0,
    RuleOfThirds = 1 << 0,
    CustomFog = 1 << 1,
}

public enum SmvmCaptureStage : uint
{
    None = 0,
    InputObserved = 1,
    BindingMatched = 2,
    CaptureRequested = 3,
    NativeFrameAwaited = 4,
    NativeFrameCaptured = 5,
    ManagedActionReturned = 6,
    DraftCreated = 7,
    KeyframeAdded = 8,
    CaptureRejected = 9,
}

public enum SmvmCaptureRejection : uint
{
    None = 0,
    ReplayUnavailable = 1,
    NotInFreeRoam = 2,
    CameraUnreadable = 3,
    NativeBackendUnavailable = 4,
    CampathOwnsCamera = 5,
    SnapshotStale = 6,
    HookFrameStale = 7,
    CaptureAlreadyPending = 8,
    ConnectionEpochChanged = 9,
    InvalidSample = 10,
    Unknown = 11,
}

[Flags]
public enum SmvmCapabilities : uint
{
    None = 0,
    ManualCamera = 1 << 0,
    RenderedRoll = 1 << 1,
    PathVisualization = 1 << 2,
    CameraSelfTest = 1 << 3,
    CampathSelfTest = 1 << 4,
}

public enum SmvmMenuAnchor : uint
{
    Left = 0,
    Right = 1,
}

public enum SmvmNotificationAnchor : uint
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
}

public enum DeadlockUiMode : uint
{
    DeadlockUi = 0,
    SmvmReplayUi = 1,
    CleanFootage = 2,
    DeathNoticesOnly = 3,
}

[Flags]
public enum DeadlockUiCapabilities : uint
{
    None = 0,
    HidePanorama = 1 << 0,
    RestorePanorama = 1 << 1,
    SmvmReplayUi = 1 << 2,
    CleanFootage = 1 << 3,
}

public enum DeadlockUiError : uint
{
    None = 0,
    ReplayUnavailable = 1,
    CommandChannelUnavailable = 2,
    UnsupportedMode = 3,
    ApplyFailed = 4,
    RestoreFailed = 5,
}

public enum SmvmReplayBarAnchor : uint
{
    Bottom = 0,
    Top = 1,
}

public static class SmvmInputCode
{
    private const uint BaseMask = 0x0000FFFF;
    private const uint ModifierMask = 0x000F0000;
    private const InputModifiers AllowedModifiers = InputModifiers.Control | InputModifiers.Alt |
                                                    InputModifiers.Shift | InputModifiers.Windows;

    public const uint None = 0;
    public const uint MouseMiddle = 0x1001;
    public const uint MouseX1 = 0x1002;
    public const uint MouseX2 = 0x1003;
    public const uint WheelUp = 0x1004;
    public const uint WheelDown = 0x1005;

    public static bool IsBindingAllowedForSlot(int slot, InputBinding binding) =>
        slot is >= 100 and <= 135 && binding.IsValid && Encode(binding) != None &&
        (slot is >= 111 and <= 121 || binding.Kind == InputBindingKind.Keyboard);

    public static uint Encode(InputBinding binding)
    {
        if (!binding.IsValid || (binding.Modifiers & ~AllowedModifiers) != 0 ||
            (binding.Kind == InputBindingKind.Keyboard && binding.Code > 0xFF))
            return None;
        var code = binding.Kind == InputBindingKind.Mouse
            ? binding.Code switch
            {
                3 => MouseMiddle,
                4 => MouseX1,
                5 => MouseX2,
                6 => WheelUp,
                7 => WheelDown,
                _ => None,
            }
            : binding.Code;
        return code | ((uint)binding.Modifiers << 16);
    }

    public static bool TryDecode(uint code, out InputBinding binding)
    {
        binding = default;
        if (code == None || (code & ~(BaseMask | ModifierMask)) != 0)
            return false;

        var modifiers = (InputModifiers)((code & ModifierMask) >> 16);
        if ((modifiers & ~AllowedModifiers) != 0)
            return false;

        var baseCode = code & BaseMask;
        binding = baseCode switch
        {
            MouseMiddle => new InputBinding(InputBindingKind.Mouse, 3, modifiers),
            MouseX1 => new InputBinding(InputBindingKind.Mouse, 4, modifiers),
            MouseX2 => new InputBinding(InputBindingKind.Mouse, 5, modifiers),
            WheelUp => new InputBinding(InputBindingKind.Mouse, 6, modifiers),
            WheelDown => new InputBinding(InputBindingKind.Mouse, 7, modifiers),
            > 0 and <= 0xFF => new InputBinding(InputBindingKind.Keyboard, baseCode, modifiers),
            _ => default,
        };
        return binding.IsValid;
    }

    public static uint ParseOrDefault(string? text, uint fallback) =>
        string.IsNullOrWhiteSpace(text)
            ? None
            : InputBinding.TryParse(text, out var binding) ? Encode(binding) : fallback;

    public static uint ParseForSlotOrDefault(int slot, string? text, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return None;
        if (InputBinding.TryParse(text, out var candidate) && IsBindingAllowedForSlot(slot, candidate))
            return Encode(candidate);
        return InputBinding.TryParse(fallback, out var defaultBinding) &&
               IsBindingAllowedForSlot(slot, defaultBinding)
            ? Encode(defaultBinding)
            : None;
    }
}

public enum SmvmActionType : uint
{
    None = 0,
    ToggleReplayPause = 1,
    SetTimescale = 2,
    SeekTick = 3,
    StepBack = 4,
    StepForward = 5,
    FreeRoam = 6,
    PreviousPlayer = 7,
    NextPlayer = 8,
    InEye = 9,
    Chase = 10,
    SetFov = 11,
    SetRoll = 12,
    SaveCamera = 13,
    RestoreCamera = 14,
    AddKeyframe = 15,
    DeleteKeyframe = 16,
    SelectKeyframe = 17,
    GoToKeyframe = 18,
    UpdateKeyframe = 19,
    ClearPath = 20,
    SetInterpolation = 21,
    SetEasing = 22,
    PlayFromStart = 23,
    PlayFromCurrent = 24,
    StopCampath = 25,
    SetEndBehavior = 26,
    UndoEdit = 27,
    RedoEdit = 28,
    ToggleToolbar = 29,
    ToggleShowPath = 30,
    ToggleShowCameras = 31,
    ToggleShowLabels = 32,
    SetBinding = 33,
    SetPathName = 34,
    SavePath = 35,
    LoadNextPath = 36,
    ToggleManualCamera = 37,
    ReacquireCamera = 38,
    CameraSelfTest = 39,
    CampathSelfTest = 40,
    ToggleNotifications = 41,
    ToggleInputTakeover = 42,
    SetUiScale = 43,
    SetMenuOpacity = 44,
    SetMenuAnchor = 45,
    SetMovementSpeed = 46,
    SetMouseSensitivity = 47,
    SetSmoothing = 48,
    ToggleInvertY = 49,
    ResetBindings = 50,
    ToggleMinimalPill = 51,
    ToggleHidePathWhilePlaying = 52,
    NewPath = 53,
    SavePathAs = 54,
    LoadPath = 55,
    ClosePath = 56,
    RecoverDraft = 57,
    DiscardDraft = 58,
    RequestPathList = 59,
    ToggleRestoreWorkspace = 60,
    SetPathLabelScale = 61,
    SetNotificationAnchor = 62,
    CaptureDiagnostic = 63,
    SetDeadlockUiMode = 64,
    RestoreDeadlockUi = 65,
    SetReplayBarScale = 66,
    SetReplayBarOpacity = 67,
    SetReplayBarAnchor = 68,
    CycleReplayInterface = 69,
    ApplyMovieMakerDefaults = 70,
    ToggleStatusHud = 71,
    SetStatusHudAnchor = 72,
    SetStatusHudScale = 73,
    SetStatusHudOpacity = 74,
    SetMovieRecordingOption = 75,
    StartMovieRecording = 76,
    StopMovieRecording = 77,
    SetMovieRecordingFps = 78,
    SetMovieRecordingPreset = 79,
    SetMovieOutputMode = 80,
    SetMovieCapturePass = 81,
    OpenMovieCaptureFolder = 82,
    SetRuleOfThirds = 83,
    SetCustomFogEnabled = 84,
    SetCustomFogValue = 85,
    SetCustomFogColor = 86,
    ResetCustomFog = 87,
    SetGreenscreenMode = 88,
    RetiredCameraAttachment = 89,
    RetiredCameraAttachmentOffset = 90,
    ChooseMovieCaptureFolder = 91,
    SetMovieOutputResolution = 92,
    SetLookEnabled = 93,
    SetLookValue = 94,
    ResetLook = 95,
    SelectLookPreset = 96,
    SaveLookPreset = 97,
    ImportLookPreset = 98,
    ExportLookPreset = 99,
    LoadLookLut = 100,
    ExitDeadlock = 101,
}

public sealed record SmvmAction(
    SmvmActionType Type,
    int Index,
    long Tick,
    double Value,
    CameraSample Camera,
    string Text,
    long ReplaySessionGeneration = 0)
{
    public static SmvmAction None { get; } = new(SmvmActionType.None, -1, -1, 0, default, string.Empty);
}

/// <summary>
/// Carries the immutable native-connection generation that produced an action.
/// The host must never restamp a delayed response with the generation of a
/// newer pipe.
/// </summary>
public sealed record SmvmActionDispatch(
    SmvmAction Action,
    long ConnectionEpoch,
    long ReplaySessionGeneration);

public sealed record SmvmSnapshot(
    SmvmSnapshotFlags Flags,
    long CurrentTick,
    long TotalTicks,
    double Timescale,
    CameraSample Camera,
    SpecCameraMode ObserverMode,
    int SelectedKeyframe,
    int KeyframeCount,
    CampathInterpolationMode Interpolation,
    CampathEasingMode Easing,
    CampathEndBehavior EndBehavior,
    CampathPlaybackState PlaybackState,
    CampathStartFailure StartFailure,
    uint MenuKey,
    uint AddKey,
    uint DeleteKey,
    uint CleanViewKey,
    double FovStep,
    uint RollLeftKey,
    uint RollRightKey,
    uint RollResetKey,
    CameraAvailability CameraAvailability,
    CameraOwnership CameraOwnership,
    SmvmCapabilities Capabilities,
    uint ForwardKey,
    uint BackwardKey,
    uint LeftKey,
    uint RightKey,
    uint UpKey,
    uint DownKey,
    uint FastKey,
    uint PrecisionKey,
    uint PlayStartKey,
    uint PlayCurrentKey,
    uint StopKey,
    uint UndoKey,
    uint RedoKey,
    uint ShowPathKey,
    uint ShowCamerasKey,
    uint ShowLabelsKey,
    double MovementSpeed,
    double BoostMultiplier,
    double PrecisionMultiplier,
    double MouseSensitivity,
    double Smoothing,
    double UiScale,
    double MenuOpacity,
    double PathLabelScale,
    SmvmMenuAnchor MenuAnchor,
    SmvmNotificationAnchor NotificationAnchor,
    string ReplayName,
    string PathName,
    string Status,
    string CameraStatus,
    CampathSessionState CampathSession,
    int SavedDocumentCount,
    uint RestoreUiKey,
    DeadlockUiMode DeadlockUiMode,
    DeadlockUiCapabilities DeadlockUiCapabilities,
    DeadlockUiError DeadlockUiError,
    int VConsolePort,
    double ReplayBarScale,
    double ReplayBarOpacity,
    SmvmReplayBarAnchor ReplayBarAnchor,
    uint CycleUiKey,
    uint ToggleFreeCameraKey,
    uint ReplayPauseKey,
    uint StepBackKey,
    uint StepForwardKey,
    SmvmNotificationAnchor StatusHudAnchor = SmvmNotificationAnchor.TopRight,
    double StatusHudScale = 1.0,
    double StatusHudOpacity = 0.92,
    ulong RecordingProfileAcknowledgementGeneration = 0,
    long ReplaySessionGeneration = 0,
    MovieRecordingFlags MovieRecordingFlags = MovieRecordingFlags.None,
    string MovieRecordingName = "",
    int MovieRecordingFps = MovieRecordingController.DefaultCaptureFps,
    MovieRecordingPreset MovieRecordingPreset = MovieRecordingPreset.EditSequence,
    MovieOutputMode MovieOutputMode = MovieOutputMode.ImageSequence,
    MovieCapturePass MovieCapturePasses = MovieCapturePass.Beauty,
    MovieToolFlags MovieToolFlags = MovieToolFlags.None,
    GreenscreenMode GreenscreenMode = GreenscreenMode.Off,
    FogConfiguration CustomFog = default,
    uint GreenscreenColorRgb = 0x00FF00,
    string MovieCapturePath = "",
    MovieOutputResolution MovieOutputResolution = MovieOutputResolution.Game,
    MovieCapturePass MovieActivePasses = MovieCapturePass.Beauty,
    MovieCompositingStage MovieCompositingStage = MovieCompositingStage.None,
    bool MovieCaptureAudio = true,
    ulong MovieExpectedFrameCount = 0,
    LookSettings? Look = null,
    uint EffectsKey = 0x14,
    uint CinematicStartKey = 0x20,
    uint PlaybackSlowerKey = 0x25,
    uint PlaybackFasterKey = 0x27,
    uint CancelKey = 0x1B,
    uint CameraSlowerKey = 0xBD,
    uint CameraFasterKey = 0xBB);

[Flags]
public enum InProcessStatusFlags : uint
{
    None = 0,
    Resolved = 1 << 0,
    HookInstalled = 1 << 1,
    PipeConnected = 1 << 2,
    ReplayGate = 1 << 3,
    OverrideRequested = 1 << 4,
    OverrideActive = 1 << 5,
    HasSample = 1 << 6,
    CommandLineReplay = 1 << 7,
    CampathActive = 1 << 8,
    CameraObserved = 1 << 9,
    CampathCompleted = 1 << 10,
    RollOverrideActive = 1 << 11,
    ManualCameraRequested = 1 << 12,
    ManualCameraActive = 1 << 13,
}

public sealed record InProcessCameraStatus(
    InProcessBackendState State,
    InProcessErrorCode Error,
    InProcessStatusFlags Flags,
    int ProcessId,
    ulong AcceptedSequence,
    ulong AppliedSequence,
    ulong HookCalls,
    long ReplayTick,
    CameraSample Camera,
    SmvmRendererBackend RendererBackend,
    SmvmRendererError RendererError,
    SmvmOverlayFlags OverlayFlags,
    uint OverlayFrameMicroseconds,
    SmvmAction Action)
{
    /// <summary>
    /// Managed-only provenance attached by <see cref="NativeReplayCameraClient"/>.
    /// It is not part of the native wire payload.
    /// </summary>
    public long SourceConnectionEpoch { get; init; }

    public bool Ready => State == InProcessBackendState.Ready &&
                         Flags.HasFlag(InProcessStatusFlags.HookInstalled);
    public bool OverrideActive => Flags.HasFlag(InProcessStatusFlags.OverrideActive);
    public bool RollOverrideActive => Flags.HasFlag(InProcessStatusFlags.RollOverrideActive);
    public bool ManualCameraRequested => Flags.HasFlag(InProcessStatusFlags.ManualCameraRequested);
    public bool ManualCameraActive => Flags.HasFlag(InProcessStatusFlags.ManualCameraActive);
    public bool CampathCompleted => Flags.HasFlag(InProcessStatusFlags.CampathCompleted);
    public bool CameraObserved => Flags.HasFlag(InProcessStatusFlags.CameraObserved) && Camera.IsValid && ReplayTick >= 0;
}

internal static class InProcessProtocol
{
    public const uint Magic = 0x4D564D43;
    public const ushort Version = 26;
    public const int HeaderSize = 28;
    public const int StatusSize = 272;
    public const int SmvmSnapshotSize = 1368;
    public const int CameraSampleSize = 56;
    public const int CampathKeyframeSize = 64;
    public const int CampathHeaderSize = 16;
    public const int CampathDocumentEntrySize = 144;
    public const int MaxCampathDocuments = 32;
    public const int MaxPayloadSize = 16 + 33 * 33 * 33 * 3 * sizeof(float);

    public static byte[] SerializeLookLut(LookSettings look)
    {
        if (!look.IsValid || look.LutRgb is null || look.LutSize == 0) throw new InvalidDataException("Invalid LUT.");
        var bytes = new byte[16 + look.LutRgb.Length * 4];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, look.LutRevision);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), look.LutSize);
        for (var i = 0; i < look.LutRgb.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16 + i * 4), look.LutRgb[i]);
        return bytes;
    }

    public static byte[] CreateMessage(
        InProcessMessageType type,
        ulong sequence,
        ReadOnlySpan<byte> payload,
        long expectedReplaySessionGeneration = 0)
    {
        if (payload.Length > MaxPayloadSize)
            throw new ArgumentOutOfRangeException(nameof(payload));
        if (expectedReplaySessionGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedReplaySessionGeneration));

        var message = new byte[HeaderSize + payload.Length];
        var span = message.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(span[12..], sequence);
        BinaryPrimitives.WriteInt64LittleEndian(span[20..], expectedReplaySessionGeneration);
        payload.CopyTo(span[HeaderSize..]);
        return message;
    }

    public static (
        InProcessMessageType Type,
        int PayloadSize,
        ulong Sequence,
        long ReplaySessionGeneration) ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderSize)
            throw new InvalidDataException("Native response header has the wrong size.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException("Native response magic does not match.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != Version)
            throw new InvalidDataException("Native response version does not match.");
        var type = (InProcessMessageType)BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        if (!Enum.IsDefined(type))
            throw new InvalidDataException("Native response type is unknown.");
        var payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]));
        if (payloadSize is < 0 or > MaxPayloadSize)
            throw new InvalidDataException("Native response payload is too large.");
        var replaySessionGeneration = BinaryPrimitives.ReadInt64LittleEndian(header[20..]);
        if (replaySessionGeneration < 0)
            throw new InvalidDataException("Native response replay generation is invalid.");
        return (
            type,
            payloadSize,
            BinaryPrimitives.ReadUInt64LittleEndian(header[12..]),
            replaySessionGeneration);
    }

    public static byte[] SerializeHello(int processId)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)processId));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), Version);
        return payload;
    }

    public static byte[] SerializeHeartbeat(bool replayActive, bool freeRoam, long replayTick, long gameTickOffset)
    {
        var payload = new byte[24];
        payload[0] = replayActive ? (byte)1 : (byte)0;
        payload[1] = freeRoam ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), replayTick);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16), gameTickOffset);
        return payload;
    }

    public static byte[] SerializeLinearCampath(LinearCampath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.IsValid)
            throw new ArgumentOutOfRangeException(nameof(path));
        return SerializeCampath(new CampathPath(new[] { path.From, path.To }));
    }

    public static byte[] SerializeCampath(
        CampathPath path,
        CampathEndBehavior endBehavior = CampathEndBehavior.StopAndRelease)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.IsValid)
            throw new ArgumentOutOfRangeException(nameof(path));

        var payload = new byte[CampathHeaderSize + (CampathKeyframeSize * path.Keyframes.Count)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)path.Keyframes.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)path.Interpolation);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)path.Easing);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), (uint)endBehavior);
        for (var index = 0; index < path.Keyframes.Count; index++)
            WriteKeyframe(payload, CampathHeaderSize + (index * CampathKeyframeSize), path.Keyframes[index]);
        return payload;
    }

    public static byte[] SerializeEditorCampath(
        IReadOnlyList<CampathKeyframe> keyframes,
        CampathInterpolationMode interpolation,
        CampathEasingMode easing)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        if (keyframes.Count is < 1 or > CampathPath.MaxKeyframes ||
            !Enum.IsDefined(interpolation) || !Enum.IsDefined(easing))
            throw new ArgumentOutOfRangeException(nameof(keyframes));
        var ordered = keyframes.OrderBy(keyframe => keyframe.DemoTick).ToArray();
        if (ordered.Any(keyframe => !keyframe.IsValid) ||
            ordered.Zip(ordered.Skip(1), (left, right) => left.DemoTick < right.DemoTick).Any(valid => !valid))
            throw new ArgumentOutOfRangeException(nameof(keyframes));
        var payload = new byte[CampathHeaderSize + (CampathKeyframeSize * ordered.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)ordered.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)interpolation);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)easing);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), (uint)CampathEndBehavior.StopAndRelease);
        for (var index = 0; index < ordered.Length; index++)
            WriteKeyframe(payload, CampathHeaderSize + (index * CampathKeyframeSize), ordered[index]);
        return payload;
    }

    public static byte[] SerializeCameraSample(CameraSample sample)
    {
        if (!sample.IsValid)
            throw new ArgumentOutOfRangeException(nameof(sample));
        var payload = new byte[CameraSampleSize];
        WriteDouble(payload, 0, sample.X);
        WriteDouble(payload, 8, sample.Y);
        WriteDouble(payload, 16, sample.Z);
        WriteDouble(payload, 24, sample.Pitch);
        WriteDouble(payload, 32, sample.Yaw);
        WriteDouble(payload, 40, sample.Roll);
        WriteDouble(payload, 48, sample.Fov);
        return payload;
    }

    public static byte[] SerializeRoll(double roll)
    {
        if (!double.IsFinite(roll) || roll is < -180.0 or > 180.0)
            throw new ArgumentOutOfRangeException(nameof(roll));
        var payload = new byte[sizeof(double)];
        WriteDouble(payload, 0, roll);
        return payload;
    }

    public static byte[] SerializeSmvmSnapshot(SmvmSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.RecordingProfileAcknowledgementGeneration > long.MaxValue ||
            snapshot.ReplaySessionGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshot));
        const MovieRecordingFlags knownMovieRecordingFlags =
            MovieRecordingFlags.DisablePostProcessing |
            MovieRecordingFlags.MuteDialogue |
            MovieRecordingFlags.WorldDepth |
            MovieRecordingFlags.Active |
            MovieRecordingFlags.Armed |
            MovieRecordingFlags.Finalizing;
        const MovieCapturePass knownMovieCapturePasses =
            MovieCapturePass.Beauty |
            MovieCapturePass.WorldDepthPfm |
            MovieCapturePass.WorldDepthAvi |
            MovieCapturePass.GreenscreenFreeCamera;
        const MovieToolFlags knownMovieToolFlags =
            MovieToolFlags.RuleOfThirds | MovieToolFlags.CustomFog;
        var customFog = snapshot.CustomFog == default
            ? FogConfiguration.Default
            : snapshot.CustomFog;
        var recordingActive = (snapshot.MovieRecordingFlags & MovieRecordingFlags.Active) != 0;
        var recordingArmed = (snapshot.MovieRecordingFlags & MovieRecordingFlags.Armed) != 0;
        var recordingFinalizing =
            (snapshot.MovieRecordingFlags & MovieRecordingFlags.Finalizing) != 0;
        if ((snapshot.MovieRecordingFlags & ~knownMovieRecordingFlags) != 0 ||
            (recordingActive ? 1 : 0) + (recordingArmed ? 1 : 0) +
                (recordingFinalizing ? 1 : 0) > 1 ||
            (recordingActive || recordingArmed || recordingFinalizing) !=
                !string.IsNullOrEmpty(snapshot.MovieRecordingName) ||
            snapshot.MovieRecordingName.Length >= 64 ||
            snapshot.MovieRecordingName.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
            snapshot.MovieRecordingFps is < MovieRecordingController.MinimumCaptureFps or
                > MovieRecordingController.MaximumCaptureFps ||
            !Enum.IsDefined(snapshot.MovieRecordingPreset) ||
            !Enum.IsDefined(snapshot.MovieOutputMode) ||
            snapshot.MovieCapturePasses == MovieCapturePass.None ||
            (snapshot.MovieCapturePasses & ~knownMovieCapturePasses) != 0 ||
            snapshot.MovieActivePasses == MovieCapturePass.None ||
            (snapshot.MovieActivePasses & ~knownMovieCapturePasses) != 0 ||
            !Enum.IsDefined(snapshot.MovieOutputResolution) ||
            !Enum.IsDefined(snapshot.MovieCompositingStage) ||
            (snapshot.MovieToolFlags & ~knownMovieToolFlags) != 0 ||
            !Enum.IsDefined(snapshot.GreenscreenMode) ||
            !customFog.IsValid ||
            snapshot.GreenscreenColorRgb > 0xFFFFFF ||
            System.Text.Encoding.UTF8.GetByteCount(snapshot.MovieCapturePath) >= 192 ||
            snapshot.MovieCapturePath.Any(static character => character is '\r' or '\n' or '\0'))
            throw new ArgumentOutOfRangeException(nameof(snapshot));
        if (snapshot.MovieExpectedFrameCount > long.MaxValue ||
            (snapshot.MovieCompositingStage == MovieCompositingStage.Chroma) !=
                (snapshot.MovieExpectedFrameCount > 0))
            throw new ArgumentOutOfRangeException(nameof(snapshot));
        var payload = new byte[SmvmSnapshotSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 14);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)snapshot.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), snapshot.CurrentTick);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16), snapshot.TotalTicks);
        WriteDouble(payload, 24, snapshot.Timescale);
        WriteCameraSample(payload, 32, snapshot.Camera);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(88), snapshot.ObserverMode switch
        {
            SpecCameraMode.InEye => 3,
            SpecCameraMode.FreeRoam => 4,
            SpecCameraMode.Chase => 6,
            _ => 0,
        });
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(92), snapshot.SelectedKeyframe);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(96), checked((uint)Math.Max(snapshot.KeyframeCount, 0)));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(100), (uint)snapshot.Interpolation);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(104), (uint)snapshot.Easing);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(108), (uint)snapshot.EndBehavior);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(112), (uint)snapshot.PlaybackState);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(116), (uint)snapshot.StartFailure);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(120), snapshot.MenuKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(124), snapshot.AddKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(128), snapshot.DeleteKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(132), snapshot.CleanViewKey);
        WriteDouble(payload, 136, snapshot.FovStep);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(144), snapshot.RollLeftKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(148), snapshot.RollRightKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(152), snapshot.RollResetKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(156), (uint)snapshot.CameraAvailability);
        WriteUtf8(payload.AsSpan(160, 64), snapshot.ReplayName);
        WriteUtf8(payload.AsSpan(224, 64), snapshot.PathName);
        WriteUtf8(payload.AsSpan(288, 128), snapshot.Status);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(416), (uint)snapshot.CameraOwnership);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(420), (uint)snapshot.Capabilities);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(424), snapshot.ForwardKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(428), snapshot.BackwardKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(432), snapshot.LeftKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(436), snapshot.RightKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(440), snapshot.UpKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(444), snapshot.DownKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(448), snapshot.FastKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(452), snapshot.PrecisionKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(456), snapshot.PlayStartKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(460), snapshot.PlayCurrentKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(464), snapshot.StopKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(468), snapshot.UndoKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(472), snapshot.RedoKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(476), snapshot.ShowPathKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(480), snapshot.ShowCamerasKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(484), snapshot.ShowLabelsKey);
        WriteDouble(payload, 488, snapshot.MovementSpeed);
        WriteDouble(payload, 496, snapshot.BoostMultiplier);
        WriteDouble(payload, 504, snapshot.PrecisionMultiplier);
        WriteDouble(payload, 512, snapshot.MouseSensitivity);
        WriteDouble(payload, 520, snapshot.Smoothing);
        WriteDouble(payload, 528, snapshot.UiScale);
        WriteDouble(payload, 536, snapshot.MenuOpacity);
        WriteDouble(payload, 544, snapshot.PathLabelScale);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(552), (uint)snapshot.MenuAnchor);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(556), (uint)snapshot.NotificationAnchor);
        WriteUtf8(payload.AsSpan(560, 80), snapshot.CameraStatus);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(640), (uint)snapshot.CampathSession);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(644),
            checked((uint)Math.Clamp(snapshot.SavedDocumentCount, 0, MaxCampathDocuments)));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(648), snapshot.RestoreUiKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(652), (uint)snapshot.DeadlockUiMode);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(656), (uint)snapshot.DeadlockUiCapabilities);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(660), (uint)snapshot.DeadlockUiError);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(664), checked((uint)snapshot.VConsolePort));
        WriteDouble(payload, 668, snapshot.ReplayBarScale);
        WriteDouble(payload, 676, snapshot.ReplayBarOpacity);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(684), (uint)snapshot.ReplayBarAnchor);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(688), snapshot.CycleUiKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(692), snapshot.ToggleFreeCameraKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(696), snapshot.ReplayPauseKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(700), snapshot.StepBackKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(704), snapshot.StepForwardKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(708), (uint)snapshot.StatusHudAnchor);
        WriteDouble(payload, 712, snapshot.StatusHudScale);
        WriteDouble(payload, 720, snapshot.StatusHudOpacity);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(728), snapshot.RecordingProfileAcknowledgementGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(
            payload.AsSpan(736), snapshot.ReplaySessionGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(744), (uint)snapshot.MovieRecordingFlags);
        WriteUtf8(payload.AsSpan(748, 64), snapshot.MovieRecordingName);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(816), checked((uint)snapshot.MovieRecordingFps));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(820), (uint)snapshot.MovieRecordingPreset);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(824), (uint)snapshot.MovieOutputMode);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(828), (uint)snapshot.MovieCapturePasses);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(832), (uint)snapshot.MovieToolFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(836), (uint)snapshot.GreenscreenMode);
        WriteDouble(payload, 840, customFog.Start);
        WriteDouble(payload, 848, customFog.End);
        WriteDouble(payload, 856, customFog.MaximumDensity);
        WriteDouble(payload, 864, customFog.Exponent);
        var fogColor = (uint)(customFog.Red << 16 |
                              customFog.Green << 8 |
                              customFog.Blue);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(872), fogColor);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(876), snapshot.GreenscreenColorRgb);
        // 880..911 are retired player-attachment fields retained as zeroed
        // padding retained from snapshot v11 so the established prefix remains stable.
        WriteUtf8(payload.AsSpan(912, 192), snapshot.MovieCapturePath);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(1104), (uint)snapshot.MovieOutputResolution);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(1108), (uint)snapshot.MovieActivePasses);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(1112), (uint)snapshot.MovieCompositingStage);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(1116), snapshot.MovieCaptureAudio ? 1u : 0u);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(1120), snapshot.MovieExpectedFrameCount);
        (snapshot.Look ?? new LookSettings()).Write(payload.AsSpan(1128));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1336), snapshot.EffectsKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1340), snapshot.CinematicStartKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1344), snapshot.PlaybackSlowerKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1348), snapshot.PlaybackFasterKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1352), snapshot.CancelKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1356), snapshot.CameraSlowerKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1360), snapshot.CameraFasterKey);
        WriteUtf8(payload.AsSpan(1268, 64), snapshot.Look?.PresetName ?? "Neutral");
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1332), snapshot.Look?.Modified == true ? 1u : 0u);
        return payload;
    }

    /// <summary>
    /// Serializes the bounded saved-path picker list (message SetCampathDocuments).
    /// Entry layout: name[64], replay[64], keyframe count u32, modified UTC ticks i64, flags u32.
    /// </summary>
    public static byte[] SerializeCampathDocuments(
        IReadOnlyList<CampathDocumentInfo> documents,
        CampathReplayIdentifier? currentReplay)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count > MaxCampathDocuments)
            throw new ArgumentOutOfRangeException(nameof(documents));

        var payload = new byte[8 + (documents.Count * CampathDocumentEntrySize)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)documents.Count);
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            var offset = 8 + (index * CampathDocumentEntrySize);
            WriteUtf8(payload.AsSpan(offset, 64), document.Name);
            WriteUtf8(payload.AsSpan(offset + 64, 64), document.ReplayIdentifier.ReplayName);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 128),
                checked((uint)Math.Max(document.KeyframeCount, 0)));
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset + 136),
                document.ModifiedUtc?.Ticks ?? 0);
            var flags = 0u;
            if (document.IsDraft)
                flags |= 1u;
            if (currentReplay is not null && document.ReplayIdentifier.Matches(currentReplay))
                flags |= 2u;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 140), flags);
        }
        return payload;
    }

    public static InProcessCameraStatus ParseStatus(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != StatusSize)
            throw new InvalidDataException("Native status payload has the wrong size.");

        var state = (InProcessBackendState)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var error = (InProcessErrorCode)BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        var flags = (InProcessStatusFlags)BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        var rendererBackend = (SmvmRendererBackend)BinaryPrimitives.ReadUInt32LittleEndian(payload[104..]);
        var rendererError = (SmvmRendererError)BinaryPrimitives.ReadUInt32LittleEndian(payload[108..]);
        var overlayFlags = (SmvmOverlayFlags)BinaryPrimitives.ReadUInt32LittleEndian(payload[112..]);
        const InProcessStatusFlags knownStatusFlags =
            InProcessStatusFlags.Resolved | InProcessStatusFlags.HookInstalled |
            InProcessStatusFlags.PipeConnected | InProcessStatusFlags.ReplayGate |
            InProcessStatusFlags.OverrideRequested | InProcessStatusFlags.OverrideActive |
            InProcessStatusFlags.HasSample | InProcessStatusFlags.CommandLineReplay |
            InProcessStatusFlags.CampathActive | InProcessStatusFlags.CameraObserved |
            InProcessStatusFlags.CampathCompleted | InProcessStatusFlags.RollOverrideActive |
            InProcessStatusFlags.ManualCameraRequested | InProcessStatusFlags.ManualCameraActive;
        const SmvmOverlayFlags knownOverlayFlags =
            SmvmOverlayFlags.HookInstalled | SmvmOverlayFlags.PresentObserved |
            SmvmOverlayFlags.Ready | SmvmOverlayFlags.MenuOpen | SmvmOverlayFlags.CleanView |
            SmvmOverlayFlags.ManualPointerActive | SmvmOverlayFlags.ManualMouseObserved |
            SmvmOverlayFlags.ManualPointerRequested | SmvmOverlayFlags.KeyboardReady |
            SmvmOverlayFlags.RelativeMouseReady | SmvmOverlayFlags.RawInputReady |
            SmvmOverlayFlags.CursorReady | SmvmOverlayFlags.ForegroundReady |
            SmvmOverlayFlags.WindowProcedureReady | SmvmOverlayFlags.EngineInputReady |
            SmvmOverlayFlags.FallbackMouseObserved |
            SmvmOverlayFlags.CreepHealthbarHookInstalled |
            SmvmOverlayFlags.CreepHealthbarSuppressionObserved |
            SmvmOverlayFlags.TowerOutlineHooksInstalled |
            SmvmOverlayFlags.TowerOutlineSuppressionObserved |
            SmvmOverlayFlags.TowerFadeOverrideInstalled |
            SmvmOverlayFlags.TowerFadeOverrideEnforced |
            SmvmOverlayFlags.CreepHealthbarHookRetrying |
            SmvmOverlayFlags.TowerOutlineHooksRetrying |
            SmvmOverlayFlags.TowerFadeOverrideRetrying |
            SmvmOverlayFlags.MovieRecordingActive |
            SmvmOverlayFlags.WorldDepthAvailable |
            SmvmOverlayFlags.WorldDepthFailed |
            SmvmOverlayFlags.MovieAviAvailable |
            SmvmOverlayFlags.WorldDepthIncomplete |
            SmvmOverlayFlags.MovieFrameReady;
        if (!Enum.IsDefined(state) || !Enum.IsDefined(error) || (flags & ~knownStatusFlags) != 0 ||
            !Enum.IsDefined(rendererBackend) || !Enum.IsDefined(rendererError) ||
            (overlayFlags & ~knownOverlayFlags) != 0)
            throw new InvalidDataException("Native status contains an unsupported state or flag.");

        var actionType = (SmvmActionType)BinaryPrimitives.ReadUInt32LittleEndian(payload[120..]);
        if (!Enum.IsDefined(actionType) || payload[263] != 0)
            throw new InvalidDataException("Native status contains an invalid SMVM action.");
        var actionReplaySessionGeneration = BinaryPrimitives.ReadInt64LittleEndian(payload[264..]);
        if (actionReplaySessionGeneration < 0)
            throw new InvalidDataException("Native status contains an invalid SMVM action replay generation.");
        var camera = new CameraSample(
            ReadDouble(payload, 48), ReadDouble(payload, 56), ReadDouble(payload, 64),
            ReadDouble(payload, 72), ReadDouble(payload, 80), ReadDouble(payload, 88),
            ReadDouble(payload, 96));
        if (flags.HasFlag(InProcessStatusFlags.CameraObserved) && !camera.IsValid)
            throw new InvalidDataException("Native status marks an invalid camera sample as observed.");

        var actionCamera = new CameraSample(
            ReadDouble(payload, 144), ReadDouble(payload, 152), ReadDouble(payload, 160),
            ReadDouble(payload, 168), ReadDouble(payload, 176), ReadDouble(payload, 184),
            ReadDouble(payload, 192));
        var actionTick = BinaryPrimitives.ReadInt64LittleEndian(payload[128..]);
        var actionValue = ReadDouble(payload, 136);
        if (!double.IsFinite(actionValue))
            throw new InvalidDataException("Native status contains a non-finite SMVM action value.");
        if (actionType == SmvmActionType.AddKeyframe && (actionTick < 0 || !actionCamera.IsValid))
            throw new InvalidDataException("Native status contains an invalid camera-capture action.");
        if (actionType == SmvmActionType.RestoreDeadlockUi && actionTick <= 0)
            throw new InvalidDataException("Native status contains an untagged emergency UI restore.");
        if (actionType == SmvmActionType.SetDeadlockUiMode && actionTick == long.MinValue)
            throw new InvalidDataException("Native status contains an invalid UI owner generation.");
        if (actionType == SmvmActionType.SetMovieRecordingOption &&
            (!Enum.IsDefined((MovieRecordingOption)BinaryPrimitives.ReadInt32LittleEndian(payload[124..])) ||
             actionValue is not 0.0 and not 1.0))
            throw new InvalidDataException("Native status contains an invalid movie-recording option action.");
        if (actionType == SmvmActionType.SetMovieRecordingFps &&
            (actionValue < MovieRecordingController.MinimumCaptureFps ||
             actionValue > MovieRecordingController.MaximumCaptureFps ||
             actionValue != Math.Truncate(actionValue)))
            throw new InvalidDataException("Native status contains an invalid movie-recording FPS action.");
        if (actionType == SmvmActionType.SetMovieRecordingPreset &&
            !Enum.IsDefined((MovieRecordingPreset)BinaryPrimitives.ReadInt32LittleEndian(payload[124..])))
            throw new InvalidDataException("Native status contains an invalid movie-recording preset action.");
        if (actionType == SmvmActionType.SetMovieOutputMode &&
            !Enum.IsDefined((MovieOutputMode)BinaryPrimitives.ReadInt32LittleEndian(payload[124..])))
            throw new InvalidDataException("Native status contains an invalid movie output action.");
        if (actionType == SmvmActionType.SetMovieCapturePass &&
            (BinaryPrimitives.ReadInt32LittleEndian(payload[124..]) is < 0 or > 3 ||
             actionValue is not 0.0 and not 1.0))
            throw new InvalidDataException("Native status contains an invalid movie pass action.");
        if (actionType == SmvmActionType.SetMovieOutputResolution &&
            !Enum.IsDefined((MovieOutputResolution)BinaryPrimitives.ReadInt32LittleEndian(payload[124..])))
            throw new InvalidDataException("Native status contains an invalid movie resolution action.");
        if ((actionType is SmvmActionType.SetRuleOfThirds or SmvmActionType.SetCustomFogEnabled) &&
            actionValue is not 0.0 and not 1.0)
            throw new InvalidDataException("Native status contains an invalid movie visual toggle.");
        if (actionType == SmvmActionType.SetCustomFogValue &&
            BinaryPrimitives.ReadInt32LittleEndian(payload[124..]) is < 0 or > 3)
            throw new InvalidDataException("Native status contains an invalid custom fog action.");
        if (actionType == SmvmActionType.SetCustomFogColor &&
            BinaryPrimitives.ReadInt32LittleEndian(payload[124..]) is < 0 or > 0xFFFFFF)
            throw new InvalidDataException("Native status contains an invalid custom fog color.");
        if (actionType == SmvmActionType.SetGreenscreenMode &&
            !Enum.IsDefined((GreenscreenMode)BinaryPrimitives.ReadInt32LittleEndian(payload[124..])))
            throw new InvalidDataException("Native status contains an invalid greenscreen mode.");
        if (actionType is SmvmActionType.RetiredCameraAttachment or
            SmvmActionType.RetiredCameraAttachmentOffset)
            throw new InvalidDataException("Native status contains a retired camera attachment action.");
        if (actionType == SmvmActionType.CaptureDiagnostic)
        {
            var stage = (SmvmCaptureStage)BinaryPrimitives.ReadInt32LittleEndian(payload[124..]);
            var rejection = (SmvmCaptureRejection)actionTick;
            if (!Enum.IsDefined(stage) || stage == SmvmCaptureStage.None || !Enum.IsDefined(rejection) ||
                (stage == SmvmCaptureStage.CaptureRejected) != (rejection != SmvmCaptureRejection.None))
                throw new InvalidDataException("Native status contains an invalid capture diagnostic.");
        }
        return new InProcessCameraStatus(
            state,
            error,
            flags,
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[12..])),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[32..]),
            BinaryPrimitives.ReadInt64LittleEndian(payload[40..]),
            camera,
            rendererBackend,
            rendererError,
            overlayFlags,
            BinaryPrimitives.ReadUInt32LittleEndian(payload[116..]),
            new SmvmAction(
                actionType,
                BinaryPrimitives.ReadInt32LittleEndian(payload[124..]),
                actionTick,
                actionValue,
                actionCamera,
                ReadUtf8(payload.Slice(200, 64)),
                actionReplaySessionGeneration));
    }

    private static void WriteDouble(Span<byte> payload, int offset, double value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(payload[offset..], BitConverter.DoubleToUInt64Bits(value));

    private static double ReadDouble(ReadOnlySpan<byte> payload, int offset) =>
        BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(payload[offset..]));

    private static void WriteKeyframe(Span<byte> payload, int offset, CampathKeyframe keyframe)
    {
        BinaryPrimitives.WriteInt64LittleEndian(payload[offset..], keyframe.DemoTick);
        SerializeCameraSample(keyframe.Camera).CopyTo(payload[(offset + 8)..]);
    }

    private static void WriteCameraSample(Span<byte> payload, int offset, CameraSample sample)
    {
        WriteDouble(payload, offset, sample.X);
        WriteDouble(payload, offset + 8, sample.Y);
        WriteDouble(payload, offset + 16, sample.Z);
        WriteDouble(payload, offset + 24, sample.Pitch);
        WriteDouble(payload, offset + 32, sample.Yaw);
        WriteDouble(payload, offset + 40, sample.Roll);
        WriteDouble(payload, offset + 48, sample.Fov);
    }

    private static void WriteUtf8(Span<byte> destination, string? value)
    {
        destination.Clear();
        if (string.IsNullOrEmpty(value) || destination.Length < 2)
            return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length - 1)).CopyTo(destination);
    }

    private static string ReadUtf8(ReadOnlySpan<byte> source)
    {
        var length = source.IndexOf((byte)0);
        if (length < 0)
            length = source.Length;
        return System.Text.Encoding.UTF8.GetString(source[..length]);
    }
}
