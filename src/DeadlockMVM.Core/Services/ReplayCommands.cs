using System.Globalization;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Builds the engine console commands for editor-level replay actions.
/// Pure string generation — no I/O — so command formatting is fully testable.
/// </summary>
public static class ReplayCommands
{
    public const double MinSpeed = 0.01;
    public const double MaxSpeed = 10.0;

    public const string Pause = "demo_pause";
    public const string Resume = "demo_resume";
    public const string TogglePause = "demo_togglepause";
    public const string QueryTimescale = "demo_timescale";
    public const string ResetLegacyHostTimescale = "host_timescale 1";

    /// <summary>Steps one tick and pauses (engine default).</summary>
    public const string StepTick = "demo_step_tick";

    /// <summary>Toggles Deadlock's built-in replay control panel.</summary>
    public const string ToggleDemoUi = "demoui";

    /// <summary>
    /// Sent without arguments on purpose: the engine then prints a usage block
    /// that includes "Currently playing &lt;tick&gt; of &lt;total&gt; ticks ... File:&lt;name&gt;".
    /// This is the only known side-effect-free way to poll live replay position.
    /// </summary>
    public const string QueryPosition = "demo_gototick";

    /// <summary>Prints demo header information (total ticks, server start tick, file name).</summary>
    public const string QueryDemoInfo = "demo_info";

    public static IReadOnlyList<string> StartDemoPaused(string gamePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        return ["+playdemo", gamePath, $"+{Pause}"];
    }

    public static IReadOnlyList<double> SpeedPresets { get; } = new[] { 0.25, 0.50, 1.00, 2.00, 4.00 };

    public static string SetTimescale(double speed)
    {
        var canonical = CanonicalizeSpeed(speed);
        return string.Create(CultureInfo.InvariantCulture, $"demo_timescale {canonical:0.########}");
    }

    public static double CanonicalizeSpeed(double speed)
    {
        ValidateSpeed(speed);
        return Math.Round(speed, 8, MidpointRounding.AwayFromZero);
    }

    public static string GotoTick(int tick)
    {
        ValidateTick(tick);
        return $"demo_gototick {tick}";
    }

    public static string StepTicks(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count == 1 ? StepTick : $"{StepTick} {count}";
    }

    public static void ValidateSpeed(double speed)
    {
        if (!double.IsFinite(speed) || speed is < MinSpeed or > MaxSpeed)
            throw new ArgumentOutOfRangeException(nameof(speed), speed,
                $"Playback speed must be between {MinSpeed} and {MaxSpeed}.");
    }

    public static void ValidateTick(int tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
    }
}
