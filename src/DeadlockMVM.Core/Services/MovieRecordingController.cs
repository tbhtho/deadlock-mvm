using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using System.Globalization;
using System.Text;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Owns DeadLockMVM's first-party cinematic recording transaction. Capture
/// rate, container and passes are typed data, never user-authored console text.
/// The native capture backend supplies synchronized TGA, WAV, direct AVI and
/// depth passes. Deadlock's dormant generic startmovie command is not required.
/// </summary>
public sealed class MovieRecordingController
{
    public const int MinimumCaptureFps = 1;
    public const int MaximumCaptureFps = 1000;
    public const int DefaultCaptureFps = 60;

    public static string DefaultCaptureRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeadlockMVM",
        "Captures");

    public const string DisablePostProcessingCommand = "r_postprocess_enable false";
    public const string RestorePostProcessingCommand = "r_postprocess_enable true";
    public const string MuteDialogueCommand = "snd_setmixer dialog vol 0";
    public const string RestoreDialogueCommand = "snd_setmixer dialog vol 1";
    public const string RestoreRealtimeTimingCommand = "host_framerate 0";

    public const MovieRecordingOptions PhysicalOptions =
        MovieRecordingOptions.DisablePostProcessing |
        MovieRecordingOptions.MuteDialogue;
    public const MovieRecordingOptions SupportedOptions = PhysicalOptions;

    private static readonly MovieRecordingOptions[] ApplyOrder =
    [
        MovieRecordingOptions.DisablePostProcessing,
        MovieRecordingOptions.MuteDialogue,
    ];

    private static readonly MovieRecordingOptions[] RestoreOrder =
    [
        MovieRecordingOptions.MuteDialogue,
        MovieRecordingOptions.DisablePostProcessing,
    ];

    private readonly ReplayController _replay;
    private readonly ILogService _log;
    private readonly Func<string> _captureRoot;
    private readonly bool _nativeOutputMonitoringEnabled;
    private readonly object _stateGate = new();
    private readonly object _transactionGate = new();
    private MovieRecordingState _state = MovieRecordingState.Default;
    private MovieRecordingOptions _appliedForRecording;
    private bool _timingApplied;
    private bool _cinematicRecording;
    private NativeOutputSession? _nativeOutputSession;
    private Task _outputFinalizationTask = Task.CompletedTask;

    public MovieRecordingController(
        ReplayController replay,
        ILogService log,
        Func<string>? captureRoot = null,
        Func<string?>? deadlockExecutablePath = null)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(log);
        _replay = replay;
        _log = log;
        _captureRoot = captureRoot ?? (() => DefaultCaptureRoot);
        // The launcher supplies this locator only after an installed client is
        // selected. Native output itself is direct; locator presence enables
        // post-stop artifact monitoring without retaining the obsolete path.
        _nativeOutputMonitoringEnabled = deadlockExecutablePath is not null;
    }

    public MovieRecordingState State
    {
        get { lock (_stateGate) return _state; }
    }

    public string CaptureRoot => Path.GetFullPath(_captureRoot());

    public Task WaitForFinalizationAsync()
    {
        lock (_transactionGate)
            return _outputFinalizationTask;
    }

    public bool Apply(MovieRecordingOptions options)
    {
        lock (_transactionGate)
        {
            if ((options & ~SupportedOptions) != MovieRecordingOptions.None)
                return Fail(MovieRecordingError.UnsupportedOptions,
                    $"Unsupported movie-recording options: {options & ~SupportedOptions}.");
            return ApplyLocked(options);
        }
    }

    public bool SetOption(MovieRecordingOptions option, bool enabled)
    {
        lock (_transactionGate)
        {
            if (!IsSingleSupportedOption(option))
                return Fail(MovieRecordingError.UnsupportedOptions,
                    $"A single supported movie-recording option is required; received {option}.");
            if (State.IsRecording || State.IsArmed || State.IsFinalizing)
                return Fail(MovieRecordingError.RecordingActive,
                    "Finish or cancel the current take before changing physical movie-recording options.");
            var current = State.EnabledOptions;
            return ApplyLocked(enabled ? current | option : current & ~option);
        }
    }

    public bool SetCaptureFps(int fps)
    {
        lock (_transactionGate)
        {
            if (fps is < MinimumCaptureFps or > MaximumCaptureFps)
                return Fail(MovieRecordingError.InvalidCaptureFps,
                    $"Capture FPS must be between {MinimumCaptureFps} and {MaximumCaptureFps}.");
            if (State.IsRecording || State.IsArmed || State.IsFinalizing)
                return Fail(MovieRecordingError.RecordingActive,
                    "Finish or cancel the current take before changing capture FPS.");
            UpdateState(state => state with
            {
                CaptureFps = fps,
                Preset = MovieRecordingPreset.Custom,
                Error = MovieRecordingError.None,
                Detail = string.Empty,
            });
            return true;
        }
    }

    public bool SetOutputMode(MovieOutputMode mode)
    {
        lock (_transactionGate)
        {
            if (!Enum.IsDefined(mode))
                return Fail(MovieRecordingError.UnsupportedOutput,
                    "The selected movie output mode is unsupported.");
            if (State.IsRecording || State.IsArmed || State.IsFinalizing)
                return Fail(MovieRecordingError.RecordingActive,
                    "Finish or cancel the current take before changing the output mode.");
            if ((State.Passes & MovieCapturePass.GreenscreenFreeCamera) != 0 &&
                mode != MovieOutputMode.Avi)
            {
                return Fail(MovieRecordingError.UnsupportedOutput,
                    "The dedicated Free Camera greenscreen is an AVI + WAV plate.");
            }
            UpdateState(state => state with
            {
                OutputMode = mode,
                Preset = MovieRecordingPreset.Custom,
                Error = MovieRecordingError.None,
                Detail = string.Empty,
            });
            return true;
        }
    }

    public bool SetOutputResolution(MovieOutputResolution resolution)
    {
        lock (_transactionGate)
        {
            if (!Enum.IsDefined(resolution))
                return Fail(MovieRecordingError.UnsupportedResolution,
                    "The selected movie output resolution is unsupported.");
            if (State.IsRecording || State.IsArmed || State.IsFinalizing)
                return Fail(MovieRecordingError.RecordingActive,
                    "Finish or cancel the current take before changing output resolution.");
            UpdateState(state => state with
            {
                OutputResolution = resolution,
                Error = MovieRecordingError.None,
                Detail = string.Empty,
            });
            return true;
        }
    }

    public bool SetPass(MovieCapturePass pass, bool enabled)
    {
        lock (_transactionGate)
        {
            if (!IsSingleSupportedPass(pass))
                return Fail(MovieRecordingError.UnsupportedPass,
                    $"A single supported recording pass is required; received {pass}.");
            if (State.IsRecording || State.IsArmed || State.IsFinalizing)
                return Fail(MovieRecordingError.RecordingActive,
                    "Finish or cancel the current take before changing recording passes.");
            if (!enabled && State.Passes == pass)
                return Fail(MovieRecordingError.CapturePassRequired,
                    "Keep at least one recording pass enabled.");
            UpdateState(state =>
            {
                var passes = enabled ? state.Passes | pass : state.Passes & ~pass;
                return state with
                {
                    Passes = passes,
                    ActivePasses = passes,
                    OutputMode = enabled && pass == MovieCapturePass.GreenscreenFreeCamera
                        ? MovieOutputMode.Avi
                        : state.OutputMode,
                    Preset = MovieRecordingPreset.Custom,
                    Error = MovieRecordingError.None,
                    Detail = string.Empty,
                };
            });
            return true;
        }
    }

    public bool ApplyPreset(MovieRecordingPreset preset)
    {
        lock (_transactionGate)
        {
            if (!Enum.IsDefined(preset))
                return Fail(MovieRecordingError.UnsupportedPreset,
                    "The selected recording preset is unsupported.");
            if (State.IsRecording || State.IsArmed || State.IsFinalizing)
                return Fail(MovieRecordingError.RecordingActive,
                    "Finish or cancel the current take before changing the recording preset.");
            var configuration = preset switch
            {
                MovieRecordingPreset.EditSequence =>
                    (60, MovieOutputMode.ImageSequence, MovieCapturePass.Beauty),
                MovieRecordingPreset.FastAvi =>
                    (60, MovieOutputMode.Avi, MovieCapturePass.Beauty),
                MovieRecordingPreset.Compositing =>
                    (60, MovieOutputMode.Avi,
                        MovieCapturePass.Beauty | MovieCapturePass.WorldDepthPfm |
                        MovieCapturePass.WorldDepthAvi |
                        MovieCapturePass.GreenscreenFreeCamera),
                MovieRecordingPreset.Custom =>
                    (State.CaptureFps, State.OutputMode, State.Passes),
                MovieRecordingPreset.Greenscreen =>
                    (60, MovieOutputMode.Avi, MovieCapturePass.GreenscreenFreeCamera),
                _ => throw new ArgumentOutOfRangeException(nameof(preset)),
            };
            UpdateState(state => state with
            {
                Preset = preset,
                CaptureFps = configuration.Item1,
                OutputMode = configuration.Item2,
                Passes = configuration.Item3,
                ActivePasses = configuration.Item3,
                Error = MovieRecordingError.None,
                Detail = string.Empty,
            });
            return true;
        }
    }

    public bool Restore() => Apply(MovieRecordingOptions.None);

    /// <summary>
    /// Reserves a take for the next Campath. No writer, directory, timing or
    /// game setting changes until Campath playback is actually committed.
    /// </summary>
    public bool ArmForCinematic(string? captureName = null)
    {
        lock (_transactionGate)
        {
            var state = State;
            if (state.IsRecording || state.IsArmed)
            {
                ClearError();
                return true;
            }
            if (state.IsFinalizing)
                return Fail(MovieRecordingError.FinalizationActive,
                    "Wait for the previous take's WAV/TGA delivery to finish.");
            if (!_replay.IsConnected)
                return Fail(MovieRecordingError.CommandChannelUnavailable,
                    "Deadlock's VConsole command channel is unavailable.");
            if (!IsActiveReplay(_replay.State))
                return Fail(MovieRecordingError.ReplayUnavailable,
                    "A live replay is required before arming a cinematic recording.");
            if (state.Passes == MovieCapturePass.None)
                return Fail(MovieRecordingError.CapturePassRequired,
                    "Enable at least one recording pass before arming the cinematic.");

            captureName ??= CreateCaptureName();
            if (!IsSafeCaptureName(captureName))
                return Fail(MovieRecordingError.InvalidCaptureName,
                    "The capture name must contain only letters, numbers, '-' or '_'.");
            if (!TryBuildTakeDirectory(captureName, out var groupDirectory))
                return Fail(MovieRecordingError.InvalidCaptureName,
                    "Choose a shorter capture folder so the complete take path fits the native writer.");

            var synchronizedCompositing = state.RequiresSynchronizedCompositing;
            var nativeCaptureName = synchronizedCompositing ? "world" : captureName;
            var takeDirectory = synchronizedCompositing
                ? Path.Combine(groupDirectory, nativeCaptureName)
                : groupDirectory;
            if (!IsBoundedTakeDirectory(takeDirectory))
                return Fail(MovieRecordingError.InvalidCaptureName,
                    "Choose a shorter capture folder so the synchronized pass paths fit the native writer.");
            var activePasses = synchronizedCompositing
                ? state.Passes & ~MovieCapturePass.GreenscreenFreeCamera
                : state.Passes;

            UpdateState(current => current with
            {
                IsArmed = true,
                CaptureName = captureName,
                CaptureDirectory = takeDirectory,
                CaptureGroupDirectory = groupDirectory,
                NativeCaptureName = nativeCaptureName,
                ActivePasses = activePasses,
                CompositingStage = synchronizedCompositing
                    ? MovieCompositingStage.World
                    : MovieCompositingStage.None,
                CaptureAudio = true,
                ExpectedFrameCount = 0,
                Error = MovieRecordingError.None,
                Detail = string.Empty,
            });
            _cinematicRecording = false;
            _log.Info($"Movie recording armed for the next cinematic: {captureName}.");
            return true;
        }
    }

    public bool StartArmedCinematic()
    {
        lock (_transactionGate)
        {
            var state = State;
            if (!state.IsArmed)
            {
                ClearError();
                return true;
            }
            return StartLocked(state.NativeCaptureName, cinematicRecording: true);
        }
    }

    public bool ArmCompositingChroma(long expectedFrameCount)
    {
        lock (_transactionGate)
        {
            var state = State;
            if (state.IsRecording || state.IsArmed || state.IsFinalizing ||
                state.CompositingStage != MovieCompositingStage.World ||
                string.IsNullOrWhiteSpace(state.CaptureName) ||
                !IsBoundedTakeDirectory(state.CaptureGroupDirectory))
            {
                return Fail(MovieRecordingError.CompositingTransitionFailed,
                    "The synchronized World pass was not ready to transition to Chroma.");
            }
            if (expectedFrameCount <= 0)
                return Fail(MovieRecordingError.CompositingTransitionFailed,
                    "The synchronized World pass did not report a usable frame count.");
            const string nativeCaptureName = "chroma";
            var takeDirectory = Path.Combine(state.CaptureGroupDirectory, nativeCaptureName);
            if (!IsBoundedTakeDirectory(takeDirectory))
                return Fail(MovieRecordingError.InvalidCaptureName,
                    "The synchronized Chroma pass path is too long for the native writer.");
            UpdateState(current => current with
            {
                IsArmed = true,
                CaptureDirectory = takeDirectory,
                NativeCaptureName = nativeCaptureName,
                ActivePasses = MovieCapturePass.GreenscreenFreeCamera,
                CompositingStage = MovieCompositingStage.Chroma,
                CaptureAudio = false,
                ExpectedFrameCount = expectedFrameCount,
                Error = MovieRecordingError.None,
                Detail = "World finalized. Chroma replay is armed.",
            });
            _cinematicRecording = false;
            _log.Info("Synchronized World pass finalized; Chroma campath replay armed.");
            return true;
        }
    }

    public void CompleteCompositingBatch(string detail, bool succeeded)
    {
        lock (_transactionGate)
        {
            UpdateState(state => state with
            {
                IsArmed = false,
                IsRecording = false,
                IsFinalizing = false,
                CaptureName = string.Empty,
                CaptureDirectory = string.Empty,
                CaptureGroupDirectory = string.Empty,
                NativeCaptureName = string.Empty,
                ActivePasses = state.Passes,
                CompositingStage = MovieCompositingStage.None,
                CaptureAudio = true,
                ExpectedFrameCount = 0,
                Error = succeeded ? MovieRecordingError.None : MovieRecordingError.CompositingTransitionFailed,
                Detail = detail,
            });
            _cinematicRecording = false;
            if (succeeded)
                _log.Info($"Synchronized compositing take ready: {detail}");
            else
                _log.Warn($"Synchronized compositing take requires review: {detail}");
        }
    }

    public bool StopCinematicRecording()
    {
        lock (_transactionGate)
        {
            if (!_cinematicRecording)
                return true;
            return StopLocked();
        }
    }

    public void AbandonForProcessBoundary()
    {
        lock (_transactionGate)
        {
            var state = State;
            var hadState = state.IsArmed || state.IsRecording ||
                           _appliedForRecording != MovieRecordingOptions.None || _timingApplied;
            lock (_stateGate)
                _state = state.IsFinalizing
                    ? state with { IsArmed = false, IsRecording = false }
                    : MovieRecordingState.Default with
                    {
                        EnabledOptions = state.EnabledOptions,
                        CaptureFps = state.CaptureFps,
                        Preset = state.Preset,
                        OutputMode = state.OutputMode,
                        OutputResolution = state.OutputResolution,
                        Passes = state.Passes,
                        ActivePasses = state.Passes,
                    };
            _appliedForRecording = MovieRecordingOptions.None;
            _timingApplied = false;
            _cinematicRecording = false;
            if (hadState)
                _log.Info("Retired movie-recording state at the Deadlock process boundary.");
        }
    }

    public bool Start(string? captureName = null)
    {
        lock (_transactionGate)
        {
            captureName ??= State.IsArmed ? State.CaptureName : CreateCaptureName();
            return StartLocked(captureName, cinematicRecording: false);
        }
    }

    public bool Stop()
    {
        lock (_transactionGate)
            return StopLocked();
    }

    public static string BuildHostFramerateCommand(int fps)
    {
        if (fps is < MinimumCaptureFps or > MaximumCaptureFps)
            throw new ArgumentOutOfRangeException(nameof(fps));
        return $"host_framerate {fps}";
    }

    private static bool IsSafeCaptureName(string value) =>
        value.Length is > 0 and < 64 && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string CreateCaptureName() =>
        $"capture_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}";

    public static string BuildCinematicCaptureName(
        string? replayName,
        long startTick,
        long endTick,
        int fps,
        DateTime? utcNow = null)
    {
        if (startTick < 0 || endTick < startTick)
            throw new ArgumentOutOfRangeException(nameof(startTick));
        if (fps is < MinimumCaptureFps or > MaximumCaptureFps)
            throw new ArgumentOutOfRangeException(nameof(fps));

        string stem;
        try
        {
            stem = Path.GetFileNameWithoutExtension(replayName?.Trim()) ?? string.Empty;
        }
        catch
        {
            stem = string.Empty;
        }
        var safeStem = new string(stem.Select(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_').ToArray()).Trim('_');
        if (safeStem.Length == 0)
            safeStem = "replay";

        var timestamp = $"{(utcNow ?? DateTime.UtcNow):yyyyMMdd-HHmmssfff}";
        var suffix = $"_t{startTick}-{endTick}_{fps}fps_{timestamp}";
        if (suffix.Length >= 63)
            suffix = $"_tb{ToBase36(startTick)}-{ToBase36(endTick)}_{fps}fps_{timestamp}";
        var stemLength = Math.Max(1, 63 - suffix.Length);
        if (safeStem.Length > stemLength)
            safeStem = safeStem[..stemLength];
        return safeStem + suffix;
    }

    private static string ToBase36(long value)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> buffer = stackalloc char[13];
        var cursor = buffer.Length;
        do
        {
            buffer[--cursor] = alphabet[(int)(value % 36)];
            value /= 36;
        }
        while (value > 0);
        return new string(buffer[cursor..]);
    }

    private bool StartLocked(string captureName, bool cinematicRecording)
    {
        if (State.IsRecording)
        {
            ClearError();
            return true;
        }
        if (State.IsFinalizing)
            return Fail(MovieRecordingError.FinalizationActive,
                "Wait for the previous take's WAV/TGA delivery to finish.");
        if (!_replay.IsConnected)
            return Fail(MovieRecordingError.CommandChannelUnavailable,
                "Deadlock's VConsole command channel is unavailable.");
        if (!IsActiveReplay(_replay.State))
            return Fail(MovieRecordingError.ReplayUnavailable,
                "A live replay is required before recording.");
        if (!IsSafeCaptureName(captureName))
            return Fail(MovieRecordingError.InvalidCaptureName,
                "The capture name must contain only letters, numbers, '-' or '_'.");

        var configuration = State;
        var activePasses = configuration.IsArmed
            ? configuration.ActivePasses
            : configuration.Passes;
        if (activePasses == MovieCapturePass.None)
            return Fail(MovieRecordingError.CapturePassRequired,
                "Enable at least one recording pass before starting the recording.");
        var takeDirectory = configuration.IsArmed &&
                            string.Equals(configuration.NativeCaptureName, captureName, StringComparison.Ordinal) &&
                            IsBoundedTakeDirectory(configuration.CaptureDirectory)
            ? configuration.CaptureDirectory
            : TryBuildTakeDirectory(captureName, out var directTakeDirectory)
                ? directTakeDirectory
                : string.Empty;
        if (takeDirectory.Length == 0)
            return Fail(MovieRecordingError.InvalidCaptureName,
                "Choose a shorter capture folder so the complete take path fits the native writer.");

        try
        {
            var writesBeautySequence =
                configuration.OutputMode != MovieOutputMode.Avi &&
                (activePasses & MovieCapturePass.Beauty) != 0;
            var audioDirectory = Path.Combine(takeDirectory, "audio");
            if (configuration.CaptureAudio)
                Directory.CreateDirectory(audioDirectory);
            if (writesBeautySequence)
                Directory.CreateDirectory(Path.Combine(takeDirectory, "color"));
            var playbackSpeed = _replay.State.Timescale is { } reportedSpeed &&
                                double.IsFinite(reportedSpeed) && reportedSpeed > 0
                ? reportedSpeed
                : 1.0;
            _nativeOutputSession = _nativeOutputMonitoringEnabled
                ? new NativeOutputSession(
                    takeDirectory,
                    captureName,
                    Path.Combine(audioDirectory, captureName + ".wav"),
                    writesBeautySequence,
                    configuration.CaptureFps,
                    playbackSpeed,
                    configuration.CaptureAudio)
                : null;

            _appliedForRecording = MovieRecordingOptions.None;
            foreach (var option in ApplyOrder)
            {
                if ((configuration.EnabledOptions & option) == 0)
                    continue;
                _replay.SendRaw(GetCommand(option, enable: true));
                _appliedForRecording |= option;
            }

            // host_framerate fixes sampling cadence. Preserve host_timescale so
            // the rendered take has exactly the cinematic speed the editor chose.
            _replay.SendRaw(BuildHostFramerateCommand(configuration.CaptureFps));
            _timingApplied = true;
            UpdateState(state => state with
            {
                IsArmed = false,
                IsRecording = true,
                CaptureName = string.IsNullOrWhiteSpace(state.CaptureName)
                    ? captureName
                    : state.CaptureName,
                CaptureDirectory = takeDirectory,
                CaptureGroupDirectory = string.IsNullOrWhiteSpace(state.CaptureGroupDirectory)
                    ? takeDirectory
                    : state.CaptureGroupDirectory,
                NativeCaptureName = captureName,
                ActivePasses = activePasses,
                Error = MovieRecordingError.None,
                Detail = string.Empty,
            });
            _cinematicRecording = cinematicRecording;
            _log.Info(
                $"Movie recording started: {captureName} ({configuration.CaptureFps} FPS, " +
                $"{configuration.OutputMode}, {activePasses}, {configuration.OutputResolution}, " +
                $"{playbackSpeed:0.###}x motion)." +
                (cinematicRecording ? " Capture is scoped to Campath playback." : string.Empty));
            return true;
        }
        catch (Exception ex)
        {
            if (_timingApplied)
            {
                try { _replay.SendRaw(RestoreRealtimeTimingCommand); }
                catch (Exception restoreException)
                {
                    _log.Warn($"Could not restore movie timing after failed start: {restoreException.Message}");
                }
                _timingApplied = false;
            }
            RestoreAppliedPhysicalSettingsBestEffort();
            _nativeOutputSession = null;
            _cinematicRecording = false;
            return Fail(MovieRecordingError.StartFailed,
                $"Could not start movie recording: {ex.Message}");
        }
    }

    private bool StopLocked()
    {
        var state = State;
        if (!state.IsRecording)
        {
            if (state.IsArmed)
            {
                UpdateState(current => current with
                {
                    IsArmed = false,
                    CaptureName = string.Empty,
                    CaptureDirectory = string.Empty,
                    CaptureGroupDirectory = string.Empty,
                    NativeCaptureName = string.Empty,
                    ActivePasses = current.Passes,
                    CompositingStage = MovieCompositingStage.None,
                    CaptureAudio = true,
                    ExpectedFrameCount = 0,
                    Error = MovieRecordingError.None,
                    Detail = string.Empty,
                });
                _cinematicRecording = false;
                _log.Info("Cinematic movie recording arm cancelled.");
            }
            else
            {
                ClearError();
            }
            return true;
        }
        if (!_replay.IsConnected)
            return Fail(MovieRecordingError.CommandChannelUnavailable,
                "Movie recording stop is waiting for Deadlock's VConsole channel.");
        try
        {
            if (_timingApplied)
            {
                _replay.SendRaw(RestoreRealtimeTimingCommand);
                _timingApplied = false;
            }
            var outputSession = _nativeOutputSession;
            _nativeOutputSession = null;
            UpdateState(current => current with
            {
                IsArmed = false,
                IsRecording = false,
                IsFinalizing = outputSession is not null,
                CaptureName = outputSession is null &&
                              current.CompositingStage == MovieCompositingStage.None
                    ? string.Empty
                    : current.CaptureName,
                NativeCaptureName = outputSession is null &&
                                    current.CompositingStage == MovieCompositingStage.None
                    ? string.Empty
                    : current.NativeCaptureName,
                ActivePasses = outputSession is null &&
                               current.CompositingStage == MovieCompositingStage.None
                    ? current.Passes
                    : current.ActivePasses,
                Error = MovieRecordingError.None,
                Detail = outputSession is null
                    ? string.Empty
                    : "Finalizing native WAV/TGA/AVI output...",
            });
            _cinematicRecording = false;
            var restored = RestoreAppliedPhysicalSettings();
            if (outputSession is not null)
                _outputFinalizationTask = FinalizeNativeOutputsAsync(outputSession);
            _log.Info(outputSession is null
                ? "Movie recording stopped and finalized."
                : "Movie recording stopped; native WAV/TGA/AVI delivery is finalizing.");
            return restored;
        }
        catch (Exception ex)
        {
            return Fail(MovieRecordingError.StopFailed,
                $"Could not stop movie recording: {ex.Message}");
        }
    }

    private bool ApplyLocked(MovieRecordingOptions target)
    {
        var current = State.EnabledOptions;
        if (current == target)
        {
            ClearError();
            return true;
        }

        if (State.IsRecording)
            return Fail(MovieRecordingError.RecordingActive,
                "Stop recording before changing movie-recording options.");
        UpdateState(state => state with
        {
            EnabledOptions = target,
            Error = MovieRecordingError.None,
            Detail = string.Empty,
        });
        _log.Info($"Movie recording setup configured: {target}.");
        return true;
    }

    private bool RestoreAppliedPhysicalSettings()
    {
        foreach (var option in RestoreOrder)
        {
            if ((_appliedForRecording & option) == 0)
                continue;
            try
            {
                _replay.SendRaw(GetCommand(option, enable: false));
                _appliedForRecording &= ~option;
            }
            catch (Exception ex)
            {
                return Fail(MovieRecordingError.RestoreFailed,
                    $"Could not restore {option}: {ex.Message}");
            }
        }
        return true;
    }

    private void RestoreAppliedPhysicalSettingsBestEffort()
    {
        foreach (var option in RestoreOrder)
        {
            if ((_appliedForRecording & option) == 0)
                continue;
            try
            {
                _replay.SendRaw(GetCommand(option, enable: false));
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not roll back {option} after failed recording start: {ex.Message}");
            }
        }
        _appliedForRecording = MovieRecordingOptions.None;
    }

    private static bool IsActiveReplay(ReplayState replay) =>
        replay.Connected &&
        !string.IsNullOrWhiteSpace(replay.ReplayName) &&
        replay.CurrentTick is not null &&
        (replay.TotalTicks is null || replay.CurrentTick < replay.TotalTicks);

    private static bool IsSingleSupportedOption(MovieRecordingOptions option) =>
        option is MovieRecordingOptions.DisablePostProcessing or MovieRecordingOptions.MuteDialogue;

    private static bool IsSingleSupportedPass(MovieCapturePass pass) =>
        pass is MovieCapturePass.Beauty or MovieCapturePass.WorldDepthPfm or
            MovieCapturePass.WorldDepthAvi or MovieCapturePass.GreenscreenFreeCamera;

    private async Task FinalizeNativeOutputsAsync(NativeOutputSession session)
    {
        await Task.Yield();
        try
        {
            var manifestPath = Path.Combine(session.TakeDirectory, "deadlockmvm_capture.txt");
            await WaitForStableFileAsync(manifestPath, 1).ConfigureAwait(false);
            var manifestLines = await File.ReadAllLinesAsync(manifestPath).ConfigureAwait(false);
            var finalizedFramesLine = manifestLines.FirstOrDefault(static line =>
                line.StartsWith("Frames finalized:", StringComparison.OrdinalIgnoreCase));
            var finalizedFramesText = finalizedFramesLine?["Frames finalized:".Length..].Trim();
            var visualPassesComplete = manifestLines.Any(static line => string.Equals(
                line,
                "All requested visual passes complete: yes",
                StringComparison.OrdinalIgnoreCase));
            if (!long.TryParse(
                    finalizedFramesText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var finalizedFrames) ||
                finalizedFrames <= 0 ||
                !visualPassesComplete)
            {
                throw new InvalidOperationException(
                    "The native writer produced no complete visual pass " +
                    "(check deadlockmvm_capture.txt for the failed pass and readiness state).");
            }
            if (session.RequiresWave && manifestLines.Any(static line => string.Equals(
                    line,
                    "Movie audio recorder started: no",
                    StringComparison.OrdinalIgnoreCase)))
            {
                var nativeError = manifestLines.FirstOrDefault(static line =>
                    line.StartsWith("Movie audio error:", StringComparison.OrdinalIgnoreCase));
                throw new InvalidOperationException(
                    "Deadlock's internal movie-audio sink could not start" +
                    (nativeError is null ? "." : $" ({nativeError})."));
            }
            if (session.RequiresWave)
                await WaitForStableFileAsync(session.WavePath, 44).ConfigureAwait(false);

            var frames = session.WritesBeautySequence
                ? Directory.EnumerateFiles(
                        Path.Combine(session.TakeDirectory, "color"),
                        session.CaptureName + "_*.tga",
                        SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
            if (session.WritesBeautySequence && frames.Length == 0)
                throw new FileNotFoundException(
                    "The native writer finalized but produced no requested TGA frames.",
                    Path.Combine(session.TakeDirectory, "color", session.CaptureName + "_*.tga"));

            var wave = session.RequiresWave ? ReadWaveFileInfo(session.WavePath) : null;
            var tgaCount = frames.Length;
            var waveBytes = session.RequiresWave ? new FileInfo(session.WavePath).Length : 0L;
            var imageDurationSeconds = tgaCount > 0
                ? tgaCount / (double)session.CaptureFps
                : (double?)null;
            var avDeltaSeconds = imageDurationSeconds is { } imageDuration && wave is not null
                ? Math.Abs(imageDuration - wave.DurationSeconds)
                : (double?)null;
            var avTimingStatus = avDeltaSeconds is not { } delta
                ? "native AVI timing is reported by deadlockmvm_capture.txt"
                : delta <= Math.Max(0.1, 2.0 / session.CaptureFps)
                    ? "within two frames"
                    : $"WARNING - {delta:F3} seconds apart";
            File.WriteAllLines(
                Path.Combine(session.TakeDirectory, "deadlockmvm_output.txt"),
                [
                    "DeadLockMVM first-party movie output",
                    "Status: complete",
                    $"WAV: {(session.RequiresWave ? session.WavePath : "not requested for this auxiliary pass")}",
                    $"WAV bytes: {waveBytes}",
                    $"WAV format: {(wave is null ? "not requested" : $"{wave.Channels} ch, {wave.SampleRate} Hz, {wave.BitsPerSample}-bit")}",
                    $"WAV duration: {(wave is null ? "not requested" : $"{wave.DurationSeconds:F6} seconds")}",
                    $"TGA frames: {tgaCount}",
                    $"TGA frame rate: {session.CaptureFps} FPS",
                    $"TGA duration: {(imageDurationSeconds is { } duration ? $"{duration:F6} seconds" : "not requested")}",
                    $"A/V timing: {avTimingStatus}",
                    $"Cinematic playback speed: {session.PlaybackSpeed:0.###}x",
                    "Source: DeadLockMVM native frame writer plus Deadlock's internal movie-audio sink",
                ]);
            lock (_transactionGate)
            {
                UpdateState(state => state with
                {
                    IsFinalizing = false,
                    CaptureName = state.CompositingStage == MovieCompositingStage.None
                        ? string.Empty
                        : state.CaptureName,
                    CaptureDirectory = state.CompositingStage == MovieCompositingStage.None
                        ? string.Empty
                        : state.CaptureDirectory,
                    NativeCaptureName = state.CompositingStage == MovieCompositingStage.None
                        ? string.Empty
                        : state.NativeCaptureName,
                    ActivePasses = state.CompositingStage == MovieCompositingStage.None
                        ? state.Passes
                        : state.ActivePasses,
                    Error = state.Error == MovieRecordingError.RestoreFailed
                        ? state.Error
                        : MovieRecordingError.None,
                    Detail = state.Error == MovieRecordingError.RestoreFailed
                        ? state.Detail
                        : (wave is null
                            ? "Auxiliary pass finalized"
                            : $"WAV finalized ({waveBytes:N0} bytes), {wave.DurationSeconds:F3}s") +
                          (tgaCount > 0
                              ? $"; {tgaCount:N0} TGA frames delivered; A/V {avTimingStatus}."
                              : "."),
                });
            }
            _log.Info($"Native movie output finalized: {session.TakeDirectory}" +
                      (tgaCount > 0 ? $" and {tgaCount} TGA frames." : "."));
        }
        catch (Exception ex)
        {
            lock (_transactionGate)
            {
                UpdateState(state => state with
                {
                    IsFinalizing = false,
                    CaptureName = state.CompositingStage == MovieCompositingStage.None
                        ? string.Empty
                        : state.CaptureName,
                    Error = state.Error == MovieRecordingError.RestoreFailed
                        ? state.Error
                        : ex is FileNotFoundException
                            ? MovieRecordingError.NativeOutputMissing
                            : MovieRecordingError.FinalizationFailed,
                    Detail = (state.Error == MovieRecordingError.RestoreFailed
                            ? state.Detail + " "
                            : string.Empty) +
                        $"Native WAV/TGA/AVI finalization failed: {ex.Message} The take was preserved at {session.TakeDirectory}.",
                });
            }
            _log.Warn($"Native WAV/TGA/AVI finalization failed: {ex.Message}. " +
                      $"Take preserved at {session.TakeDirectory}.");
        }
    }

    private static async Task WaitForStableFileAsync(string path, long minimumLength)
    {
        long previousLength = -1;
        var stableChecks = 0;
        for (var attempt = 0; attempt < 400 && stableChecks < 2; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    var length = new FileInfo(path).Length;
                    stableChecks = length >= minimumLength && length == previousLength
                        ? stableChecks + 1
                        : 0;
                    previousLength = length;
                }
            }
            catch (IOException)
            {
                stableChecks = 0;
            }
            if (stableChecks < 2)
                await Task.Delay(50).ConfigureAwait(false);
        }
        if (stableChecks < 2)
            throw new FileNotFoundException(
                $"The native writer did not finalize the requested artifact within 20 seconds: {path}",
                path);
    }

    private sealed record NativeOutputSession(
        string TakeDirectory,
        string CaptureName,
        string WavePath,
        bool WritesBeautySequence,
        int CaptureFps,
        double PlaybackSpeed,
        bool RequiresWave);

    private sealed record WaveFileInfo(
        ushort Channels,
        uint SampleRate,
        ushort BitsPerSample,
        uint DataBytes,
        double DurationSeconds);

    private static WaveFileInfo ReadWaveFileInfo(string path)
    {
        const uint riff = 0x46464952;
        const uint wave = 0x45564157;
        const uint formatChunk = 0x20746D66;
        const uint dataChunk = 0x61746164;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (stream.Length < 44 || reader.ReadUInt32() != riff)
            throw new InvalidDataException("Deadlock's finalized audio is not a RIFF WAV file.");
        _ = reader.ReadUInt32();
        if (reader.ReadUInt32() != wave)
            throw new InvalidDataException("Deadlock's finalized audio has no WAVE signature.");

        ushort channels = 0;
        ushort bitsPerSample = 0;
        uint sampleRate = 0;
        uint byteRate = 0;
        uint dataBytes = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = reader.ReadUInt32();
            var chunkBytes = reader.ReadUInt32();
            var chunkEnd = checked(stream.Position + chunkBytes);
            if (chunkEnd > stream.Length)
                throw new InvalidDataException("Deadlock's finalized WAV contains a truncated chunk.");
            if (chunkId == formatChunk)
            {
                if (chunkBytes < 16)
                    throw new InvalidDataException("Deadlock's finalized WAV has a truncated format chunk.");
                var encoding = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadUInt32();
                byteRate = reader.ReadUInt32();
                _ = reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
                if (encoding is not (1 or 3))
                    throw new InvalidDataException($"Deadlock's WAV encoding {encoding} is not PCM or IEEE float.");
            }
            else if (chunkId == dataChunk)
            {
                dataBytes = chunkBytes;
            }
            stream.Position = chunkEnd + (chunkBytes & 1u);
        }
        if (channels == 0 || sampleRate == 0 || byteRate == 0 ||
            bitsPerSample == 0 || dataBytes == 0)
        {
            throw new InvalidDataException("Deadlock's finalized WAV has no usable format or audio data.");
        }
        return new WaveFileInfo(
            channels,
            sampleRate,
            bitsPerSample,
            dataBytes,
            dataBytes / (double)byteRate);
    }

    private static string GetCommand(MovieRecordingOptions option, bool enable) =>
        (option, enable) switch
        {
            (MovieRecordingOptions.DisablePostProcessing, true) => DisablePostProcessingCommand,
            (MovieRecordingOptions.DisablePostProcessing, false) => RestorePostProcessingCommand,
            (MovieRecordingOptions.MuteDialogue, true) => MuteDialogueCommand,
            (MovieRecordingOptions.MuteDialogue, false) => RestoreDialogueCommand,
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, "Option has no physical command."),
        };

    private void UpdateState(Func<MovieRecordingState, MovieRecordingState> update)
    {
        lock (_stateGate)
            _state = update(_state);
    }

    private void ClearError() => UpdateState(state =>
        state.Error == MovieRecordingError.None && state.Detail.Length == 0
            ? state
            : state with { Error = MovieRecordingError.None, Detail = string.Empty });

    private bool Fail(MovieRecordingError error, string detail)
    {
        UpdateState(state => state with { Error = error, Detail = detail });
        _log.Warn($"Movie recording setup: {detail}");
        return false;
    }

    private bool TryBuildTakeDirectory(string captureName, out string takeDirectory)
    {
        try
        {
            takeDirectory = Path.GetFullPath(Path.Combine(CaptureRoot, captureName));
            return IsBoundedTakeDirectory(takeDirectory);
        }
        catch
        {
            takeDirectory = string.Empty;
            return false;
        }
    }

    private static bool IsBoundedTakeDirectory(string takeDirectory) =>
        !string.IsNullOrWhiteSpace(takeDirectory) &&
        takeDirectory.IndexOfAny(['"', '\r', '\n', ';']) < 0 &&
        System.Text.Encoding.UTF8.GetByteCount(takeDirectory) < 192;
}

[Flags]
public enum MovieRecordingOptions : uint
{
    None = 0,
    DisablePostProcessing = 1 << 0,
    MuteDialogue = 1 << 1,
}

public enum MovieRecordingPreset : uint
{
    EditSequence = 0,
    FastAvi = 1,
    Compositing = 2,
    Custom = 3,
    Greenscreen = 4,
}

public enum MovieOutputMode : uint
{
    ImageSequence = 0,
    Avi = 1,
    Both = 2,
}

public enum MovieOutputResolution : uint
{
    Game = 0,
    FullHd = 1,
}

public enum MovieCompositingStage : uint
{
    None = 0,
    World = 1,
    Chroma = 2,
}

[Flags]
public enum MovieCapturePass : uint
{
    None = 0,
    Beauty = 1 << 0,
    WorldDepthPfm = 1 << 1,
    WorldDepthAvi = 1 << 2,
    GreenscreenFreeCamera = 1 << 3,
}

public enum MovieRecordingError
{
    None = 0,
    ReplayUnavailable = 1,
    CommandChannelUnavailable = 2,
    UnsupportedOptions = 3,
    ApplyFailed = 4,
    RestoreFailed = 5,
    InvalidCaptureName = 6,
    StartFailed = 7,
    StopFailed = 8,
    RecordingActive = 9,
    InvalidCaptureFps = 10,
    UnsupportedOutput = 11,
    UnsupportedPass = 12,
    UnsupportedPreset = 13,
    CapturePassRequired = 14,
    NativeOutputUnavailable = 15,
    FinalizationActive = 16,
    NativeOutputMissing = 17,
    FinalizationFailed = 18,
    UnsupportedResolution = 19,
    CompositingTransitionFailed = 20,
}

public sealed record MovieRecordingState(
    MovieRecordingOptions EnabledOptions,
    MovieRecordingError Error,
    string Detail,
    bool IsArmed,
    bool IsRecording,
    string CaptureName,
    string CaptureDirectory,
    bool IsFinalizing,
    int CaptureFps,
    MovieRecordingPreset Preset,
    MovieOutputMode OutputMode,
    MovieOutputResolution OutputResolution,
    MovieCapturePass Passes,
    MovieCapturePass ActivePasses,
    MovieCompositingStage CompositingStage,
    string NativeCaptureName,
    string CaptureGroupDirectory,
    bool CaptureAudio,
    long ExpectedFrameCount)
{
    public static MovieRecordingState Default { get; } = new(
        MovieRecordingOptions.None,
        MovieRecordingError.None,
        string.Empty,
        false,
        false,
        string.Empty,
        string.Empty,
        false,
        MovieRecordingController.DefaultCaptureFps,
        MovieRecordingPreset.EditSequence,
        MovieOutputMode.ImageSequence,
        MovieOutputResolution.Game,
        MovieCapturePass.Beauty,
        MovieCapturePass.Beauty,
        MovieCompositingStage.None,
        string.Empty,
        string.Empty,
        true,
        0);

    public bool WorldDepthPassEnabled =>
        (Passes & (MovieCapturePass.WorldDepthPfm | MovieCapturePass.WorldDepthAvi |
                   MovieCapturePass.GreenscreenFreeCamera)) != 0;

    public bool RequiresSynchronizedCompositing =>
        (Passes & MovieCapturePass.Beauty) != 0 &&
        (Passes & MovieCapturePass.GreenscreenFreeCamera) != 0;

    public bool IsEnabled(MovieRecordingOptions option) =>
        option != MovieRecordingOptions.None && (EnabledOptions & option) == option;
}
