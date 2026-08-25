using System.Text.RegularExpressions;
using System.Globalization;
using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Services;

/// <summary>
/// Extracts replay state from engine console output lines. All patterns were
/// verified against live Deadlock demo playback.
///
/// Known line shapes (Deadlock build 10725+):
///   "Currently playing 42428 of 146926 ticks. Minutes:38.26 File:replays/75438101-6448.dem"
///   "CGameRules - paused on tick 29382"          (game tick = demo tick + server_start_tick)
///   "CGameRules - unpaused on tick 29385"
///   "[Demo] Demo paused at engine time 27998, demo tick 20001"
///   "Demo Skipping finished at tick 21000"
///   "Demo Skipping: skipping forward to demo tick 21000 (game tick 28997) from current ..."
///   "server_start_tick: 7997"                    (demo_info output)
///   "playback_ticks: 146926"                     (demo_info output)
/// </summary>
public sealed partial class ReplayStateParser
{
    private int? _gameTickOffset;

    /// <summary>Engine game tick minus demo-timeline tick, learned from demo_info/output.</summary>
    public int? GameTickOffset => _gameTickOffset;

    public void ResetGameTickOffset() => _gameTickOffset = null;

    public bool TryParseSeekCompletedTick(string line, out int tick)
    {
        var match = SkippingFinishedLine().Match(line ?? string.Empty);
        tick = match.Success ? int.Parse(match.Groups[1].Value) : 0;
        return match.Success;
    }

    [GeneratedRegex(@"Currently playing (\d+) of (\d+) ticks\. Minutes:[\d.]+ File:(\S+)")]
    private static partial Regex PositionLine();

    [GeneratedRegex(@"CGameRules - (paused|unpaused) on tick (\d+)")]
    private static partial Regex GameRulesPauseLine();

    [GeneratedRegex(@"Demo paused at engine time \d+, demo tick (\d+)")]
    private static partial Regex DemoPausedLine();

    [GeneratedRegex(@"Demo Skipping finished at tick (\d+)")]
    private static partial Regex SkippingFinishedLine();

    [GeneratedRegex(@"skipping forward to demo tick (\d+) \(game tick (\d+)\)")]
    private static partial Regex SkippingForwardLine();

    [GeneratedRegex(@"server_start_tick: (\d+)")]
    private static partial Regex ServerStartTickLine();

    [GeneratedRegex(@"playback_ticks: (\d+)")]
    private static partial Regex PlaybackTicksLine();

    [GeneratedRegex(
        @"(?:\[Demo\]\s+)?playing demo from\s+'([^'\r\n]+\.dem)'",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DemoPlaybackStartedLine();

    [GeneratedRegex(
        @"\[HostStateManager\]\s+Host activate:\s+Playing Demo(?:\s+\([^\r\n)]*\))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DemoHostActivatedLine();

    [GeneratedRegex(@"demo_timescale\s*=\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)")]
    private static partial Regex DemoTimescaleLine();

    /// <summary>
    /// Source emits this only when a demo instance actually begins playback,
    /// including a reload of the same file. It is not emitted by seeking.
    /// </summary>
    public bool TryParseReplaySessionStarted(string line, out string replayName)
    {
        var match = DemoPlaybackStartedLine().Match(line ?? string.Empty);
        replayName = match.Success ? match.Groups[1].Value : string.Empty;
        return match.Success;
    }

    /// <summary>
    /// Parses one console output line and returns a partial state containing
    /// only the values the line reveals; null fields mean "nothing learned".
    /// Returns null for lines that carry no replay state.
    /// </summary>
    public ReplayState? ParseLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        if (DemoHostActivatedLine().IsMatch(line))
            return new ReplayState { PlaybackHostActive = true };

        var position = PositionLine().Match(line);
        if (position.Success)
        {
            return new ReplayState
            {
                CurrentTick = int.Parse(position.Groups[1].Value),
                TotalTicks = int.Parse(position.Groups[2].Value),
                ReplayName = position.Groups[3].Value,
            };
        }

        var gameRules = GameRulesPauseLine().Match(line);
        if (gameRules.Success)
        {
            var gameTick = int.Parse(gameRules.Groups[2].Value);
            return new ReplayState
            {
                IsPaused = gameRules.Groups[1].Value == "paused",
                CurrentTick = ToDemoTick(gameTick),
            };
        }

        var demoPaused = DemoPausedLine().Match(line);
        if (demoPaused.Success)
        {
            return new ReplayState
            {
                IsPaused = true,
                CurrentTick = int.Parse(demoPaused.Groups[1].Value),
            };
        }

        var finished = SkippingFinishedLine().Match(line);
        if (finished.Success)
        {
            return new ReplayState
            {
                IsPaused = false,
                CurrentTick = int.Parse(finished.Groups[1].Value),
            };
        }

        var forward = SkippingForwardLine().Match(line);
        if (forward.Success)
        {
            var demoTick = int.Parse(forward.Groups[1].Value);
            var gameTick = int.Parse(forward.Groups[2].Value);
            if (gameTick > demoTick)
                _gameTickOffset = gameTick - demoTick;

            return new ReplayState { CurrentTick = demoTick };
        }

        var serverStart = ServerStartTickLine().Match(line);
        if (serverStart.Success)
        {
            _gameTickOffset = int.Parse(serverStart.Groups[1].Value);
            return null; // calibration only, no user-visible state change
        }

        var playbackTicks = PlaybackTicksLine().Match(line);
        if (playbackTicks.Success)
        {
            return new ReplayState
            {
                TotalTicks = int.Parse(playbackTicks.Groups[1].Value),
            };
        }

        var demoTimescale = DemoTimescaleLine().Match(line);
        if (demoTimescale.Success && double.TryParse(
                demoTimescale.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var timescale) && double.IsFinite(timescale))
        {
            return new ReplayState { Timescale = timescale };
        }

        return null;
    }

    private int? ToDemoTick(int gameTick)
        => _gameTickOffset is { } offset ? gameTick - offset : null;
}
