using System.Text;

namespace DeadlockMVM.Core;

/// <summary>
/// Small helpers for tokenizing and rendering command lines. Used to parse the
/// user's extra launch arguments and to build the preview/log string.
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Splits a raw command-line string into arguments, honoring double quotes.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return Array.Empty<string>();

        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    /// <summary>Quotes a single argument if it needs it to survive a shell boundary.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length == 0)
            return "\"\"";

        if (argument.Any(char.IsWhiteSpace) || argument.Contains('"'))
            return "\"" + argument.Replace("\"", "\\\"") + "\"";

        return argument;
    }

    /// <summary>Joins arguments into a single display-ready command line.</summary>
    public static string Join(IEnumerable<string> arguments)
    {
        return string.Join(' ', arguments.Select(Quote));
    }
}
