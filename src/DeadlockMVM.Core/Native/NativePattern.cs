using System.Globalization;

namespace DeadlockMVM.Core.Native;

/// <summary>
/// An IDA-style byte pattern: <see cref="Values"/> holds the byte to compare at each
/// position and <see cref="Mask"/> marks which positions must match (1 = fixed,
/// 0 = wildcard).
/// </summary>
public sealed record NativePattern(byte[] Values, byte[] Mask)
{
    /// <summary>
    /// Parses whitespace-separated hex bytes; <c>?</c> or <c>??</c> denote wildcards,
    /// e.g. <c>"48 8D 3D ?? ?? ?? ??"</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The text is empty, contains a malformed
    /// token, or has no fixed byte to anchor a scan on.</exception>
    public static NativePattern Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Pattern text is empty.", nameof(text));

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var values = new byte[tokens.Length];
        var mask = new byte[tokens.Length];
        var hasFixedByte = false;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token is "?" or "??")
            {
                mask[i] = 0;
                continue;
            }

            if (token.Length != 2 ||
                !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out values[i]))
            {
                throw new ArgumentException(
                    $"Malformed pattern token '{token}'; expected two hex digits or '?'/'??'.", nameof(text));
            }

            mask[i] = 1;
            hasFixedByte = true;
        }

        if (!hasFixedByte)
            throw new ArgumentException("Pattern must contain at least one fixed byte.", nameof(text));

        return new NativePattern(values, mask);
    }
}

/// <summary>
/// Thrown when a signature does not resolve to exactly one match. Carries the
/// observed <see cref="MatchCount"/> (capped at 2 for "more than one").
/// </summary>
public sealed class SignatureResolutionException : Exception
{
    public SignatureResolutionException(string message, int matchCount)
        : base(message) => MatchCount = matchCount;

    public int MatchCount { get; }
}
