using DeadlockMVM.Core.Contracts;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// First-party movie visual controls. Fog is immutable typed state consumed by
/// the native D3D11 compositor; the remaining view-model preview uses a fixed
/// Deadlock command pair and never accepts arbitrary console text.
/// </summary>
public sealed class MovieVisualController
{
    public const string RestoreWorldCommand = "r_drawblankworld false";
    public const string BlankWorldCommand = "r_drawblankworld true";
    public const string HideWorldGeometryCommand = "r_drawworld false";
    public const string ShowWorldGeometryCommand = "r_drawworld true";
    public const string HideSkyboxCommand = "r_drawskybox false";
    public const string ShowSkyboxCommand = "r_drawskybox true";
    public const string ShowViewModelCommand = "r_drawviewmodel true";
    public const string HideViewModelCommand = "r_drawviewmodel false";
    public const string HideParticlesCommand = "r_drawparticles false";
    public const string ShowParticlesCommand = "r_drawparticles true";
    public const string HideRopesCommand = "r_drawropes false";
    public const string ShowRopesCommand = "r_drawropes true";
    public const string HideTracersCommand = "r_drawtracers false";
    public const string ShowTracersCommand = "r_drawtracers true";
    public const string HideDecalsCommand = "r_drawdecals false";
    public const string ShowDecalsCommand = "r_drawdecals true";

    private readonly ReplayController _replay;
    private readonly ILogService _log;
    private readonly object _gate = new();
    private MovieVisualState _state = MovieVisualState.Default;
    private bool _fogApplied;
    private bool _greenscreenApplied;

    public MovieVisualController(ReplayController replay, ILogService log)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(log);
        _replay = replay;
        _log = log;
    }

    public MovieVisualState State
    {
        get { lock (_gate) return _state; }
    }

    public void LoadConfiguration(
        bool ruleOfThirds,
        bool fogEnabled,
        FogConfiguration fog,
        GreenscreenMode greenscreen)
    {
        lock (_gate)
        {
            _fogApplied = false;
            _greenscreenApplied = false;
            _state = new MovieVisualState(
                ruleOfThirds,
                fogEnabled,
                fog.IsPractical ? fog : FogConfiguration.Default,
                Enum.IsDefined(greenscreen) ? greenscreen : GreenscreenMode.Off,
                string.Empty);
        }
    }

    public bool SetRuleOfThirds(bool enabled)
    {
        lock (_gate)
        {
            _state = _state with { RuleOfThirds = enabled, Error = string.Empty };
            return true;
        }
    }

    public bool SetGreenscreenMode(GreenscreenMode mode)
    {
        if (!Enum.IsDefined(mode))
            return Fail("Unsupported greenscreen mode.");
        if (!CanCommandReplay())
            return Fail("A live replay and VConsole connection are required for greenscreen preview.");
        try
        {
            SendGreenscreenMode(mode);
            lock (_gate)
            {
                _state = _state with { Greenscreen = mode, Error = string.Empty };
                _greenscreenApplied = mode != GreenscreenMode.Off;
            }
            _log.Info($"Greenscreen preview changed to {mode}.");
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"Could not change greenscreen preview: {ex.Message}");
        }
    }

    public bool SetFogEnabled(bool enabled)
    {
        lock (_gate)
        {
            _state = _state with { FogEnabled = enabled, Error = string.Empty };
            _fogApplied = enabled;
        }
        _log.Info(enabled
            ? "Native depth fog compositor enabled."
            : "Native depth fog compositor disabled.");
        return true;
    }

    public bool SetFog(FogConfiguration fog)
    {
        if (!fog.IsPractical)
            return Fail("Custom fog values must stay inside the visible preset range.");
        lock (_gate)
            _state = _state with { Fog = fog, Error = string.Empty };
        return true;
    }

    public bool ApplyFogPreset(FogConfiguration fog)
    {
        if (!fog.IsPractical)
            return Fail("Custom fog values must stay inside the visible preset range.");
        lock (_gate)
            _state = _state with { Fog = fog, Error = string.Empty };
        return true;
    }

    public void AbandonForProcessBoundary()
    {
        lock (_gate)
        {
            _fogApplied = false;
            _greenscreenApplied = false;
            _state = _state with { Error = string.Empty };
        }
    }

    public bool ReassertConfiguredVisuals()
    {
        MovieVisualState state;
        bool applyFog;
        bool applyGreenscreen;
        lock (_gate)
        {
            state = _state;
            applyFog = state.FogEnabled && !_fogApplied;
            applyGreenscreen = state.Greenscreen != GreenscreenMode.Off && !_greenscreenApplied;
        }
        if (!applyFog && !applyGreenscreen)
            return true;
        if (applyGreenscreen && !CanCommandReplay())
            return Fail("A live replay and VConsole connection are required to restore movie visuals.");
        try
        {
            if (applyFog)
            {
                lock (_gate) _fogApplied = true;
            }
            if (applyGreenscreen)
            {
                SendGreenscreenMode(state.Greenscreen);
                lock (_gate) _greenscreenApplied = true;
            }
            lock (_gate) _state = _state with { Error = string.Empty };
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"Could not restore movie visuals: {ex.Message}");
        }
    }

    public bool SuspendPhysicalVisuals()
    {
        bool greenscreenApplied;
        lock (_gate)
        {
            greenscreenApplied = _greenscreenApplied;
            _fogApplied = false;
            _greenscreenApplied = false;
        }
        // Fog disappears with the native replay snapshot and needs no engine
        // inverse. Only the view-model preview owns physical console state.
        if (!greenscreenApplied)
            return true;
        if (!CanCommandGame())
            return false;
        try
        {
            if (greenscreenApplied)
                SendGreenscreenMode(GreenscreenMode.Off);
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"Could not suspend movie visuals: {ex.Message}");
        }
    }

    public bool RestorePhysicalVisuals()
    {
        var canCommandGame = CanCommandGame();
        try
        {
            if (canCommandGame)
            {
                _replay.SendRaw(RestoreWorldCommand);
                _replay.SendRaw(ShowWorldGeometryCommand);
                _replay.SendRaw(ShowSkyboxCommand);
                _replay.SendRaw(ShowViewModelCommand);
                _replay.SendRaw(ShowParticlesCommand);
                _replay.SendRaw(ShowRopesCommand);
                _replay.SendRaw(ShowTracersCommand);
                _replay.SendRaw(ShowDecalsCommand);
            }
            lock (_gate)
            {
                _state = _state with
                {
                    FogEnabled = false,
                    Greenscreen = GreenscreenMode.Off,
                    Error = string.Empty,
                };
                _fogApplied = false;
                _greenscreenApplied = false;
            }
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"Could not restore movie visuals: {ex.Message}");
        }
    }

    private void SendGreenscreenMode(GreenscreenMode mode)
    {
        switch (mode)
        {
            case GreenscreenMode.Off:
                _replay.SendRaw(RestoreWorldCommand);
                _replay.SendRaw(ShowWorldGeometryCommand);
                _replay.SendRaw(ShowSkyboxCommand);
                _replay.SendRaw(ShowViewModelCommand);
                _replay.SendRaw(ShowParticlesCommand);
                _replay.SendRaw(ShowRopesCommand);
                _replay.SendRaw(ShowTracersCommand);
                _replay.SendRaw(ShowDecalsCommand);
                break;
            case GreenscreenMode.FreeCamera:
                // r_drawblankworld needs the rendering-world session alive: it
                // substitutes the game world while preserving the scene depth
                // pass used to isolate subjects. Combining it with
                // r_drawworld false bypassed that session entirely, leaving the
                // native compositor without a DSV and producing a zero-frame
                // Chroma folder. The remaining switches remove non-subject
                // effects before the reversed-Z far-plane key fill.
                _replay.SendRaw(ShowWorldGeometryCommand);
                _replay.SendRaw(BlankWorldCommand);
                _replay.SendRaw(HideSkyboxCommand);
                _replay.SendRaw(HideViewModelCommand);
                _replay.SendRaw(HideParticlesCommand);
                _replay.SendRaw(HideRopesCommand);
                _replay.SendRaw(HideTracersCommand);
                _replay.SendRaw(HideDecalsCommand);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private bool CanCommandReplay() =>
        CanCommandGame() &&
        !string.IsNullOrWhiteSpace(_replay.State.ReplayName);

    private bool CanCommandGame() =>
        _replay.IsConnected && _replay.State.Connected;

    private bool Fail(string detail)
    {
        lock (_gate)
            _state = _state with { Error = detail };
        _log.Warn($"Movie visual setup: {detail}");
        return false;
    }
}

public enum GreenscreenMode : uint
{
    Off = 0,
    FreeCamera = 1,
}

public readonly record struct FogConfiguration(
    double Start,
    double End,
    double MaximumDensity,
    double Exponent,
    byte Red,
    byte Green,
    byte Blue)
{
    public static FogConfiguration Default { get; } =
        new(0, 1200, 0.9, 1.5, 128, 144, 160);

    public bool IsValid =>
        double.IsFinite(Start) && Start is >= -100_000 and <= 100_000 &&
        double.IsFinite(End) && End is >= -100_000 and <= 100_000 && End >= Start &&
        double.IsFinite(MaximumDensity) && MaximumDensity is >= 0 and <= 1 &&
        double.IsFinite(Exponent) && Exponent is >= 0.01 and <= 10;

    public bool IsPractical => IsValid &&
        Start is >= -500 and <= 8_000 &&
        End is >= 100 and <= 12_000 &&
        End - Start >= 100 &&
        MaximumDensity is >= 0.05 and <= 1 &&
        Exponent is >= 0.1 and <= 4;
}

public sealed record MovieVisualState(
    bool RuleOfThirds,
    bool FogEnabled,
    FogConfiguration Fog,
    GreenscreenMode Greenscreen,
    string Error)
{
    public static MovieVisualState Default { get; } = new(
        false,
        false,
        FogConfiguration.Default,
        GreenscreenMode.Off,
        string.Empty);
}
