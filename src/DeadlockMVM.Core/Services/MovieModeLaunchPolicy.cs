namespace DeadlockMVM.Core.Services;

/// <summary>
/// Keeps replay safety arguments outside the editable additional-argument field
/// and builds the one authoritative launcher-created replay command line.
/// </summary>
public static class MovieModeLaunchPolicy
{
    public static string NormalizeAdditionalArguments(string? arguments)
    {
        var tokens = CommandLine.Tokenize(arguments);
        var normalized = new List<string>(tokens.Count);
        for (var index = 0; index < tokens.Count; index++)
        {
            var argument = tokens[index];
            if (IsRequired(argument) ||
                string.Equals(argument, "-secure", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "+demo_pause", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(argument, "+playdemo", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < tokens.Count)
                    index++;
                continue;
            }
            normalized.Add(argument);
        }
        return CommandLine.Join(normalized);
    }

    public static IReadOnlyList<string> BuildPreviewArguments(string? additionalArguments) =>
        DeadlockConstants.MovieModeArguments
            .Concat(CommandLine.Tokenize(NormalizeAdditionalArguments(additionalArguments)))
            .ToArray();

    public static IReadOnlyList<string> BuildReplayArguments(
        string? additionalArguments,
        string gamePath) =>
        BuildPreviewArguments(additionalArguments)
            .Concat(ReplayCommands.StartDemoPaused(gamePath))
            .ToArray();

    public static bool HasRequiredReplayArguments(IEnumerable<string> arguments)
    {
        var tokens = arguments.ToArray();
        var insecure = tokens.Any(argument =>
            string.Equals(argument, "-insecure", StringComparison.OrdinalIgnoreCase));
        var secure = tokens.Any(argument =>
            string.Equals(argument, "-secure", StringComparison.OrdinalIgnoreCase));
        var replay = false;
        for (var index = 0; index + 1 < tokens.Length; index++)
        {
            if (!string.Equals(tokens[index], "+playdemo", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(tokens[index + 1]) ||
                tokens[index + 1][0] is '+' or '-')
                continue;
            replay = true;
            break;
        }
        return insecure && !secure && replay;
    }

    private static bool IsRequired(string argument) =>
        DeadlockConstants.MovieModeArguments.Any(required =>
            string.Equals(argument, required, StringComparison.OrdinalIgnoreCase));
}
