namespace DeadlockMVM.Core.Native;

/// <summary>
/// Scans byte buffers for <see cref="NativePattern"/> matches. Pure and
/// allocation-light: the leading fixed-byte run accelerates the search via
/// <see cref="MemoryExtensions.IndexOf"/> and candidates are verified against
/// the full mask.
/// </summary>
public static class PatternScanner
{
    /// <summary>
    /// Returns the RVAs (=<paramref name="haystackBaseRva"/> + match index) of every
    /// match start, stopping once <paramref name="maxResults"/> have been collected.
    /// </summary>
    public static IReadOnlyList<long> FindAll(
        ReadOnlySpan<byte> haystack, long haystackBaseRva, NativePattern pattern, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (maxResults < 1)
            throw new ArgumentOutOfRangeException(nameof(maxResults));

        var values = pattern.Values;
        var mask = pattern.Mask;
        if (values.Length == 0 || values.Length != mask.Length)
            throw new ArgumentException("Pattern values and mask must be non-empty and equal length.", nameof(pattern));

        var results = new List<long>();
        if (haystack.Length < values.Length)
            return results;

        // Anchor on the leading fixed-byte run; if the pattern starts with
        // wildcards, fall back to the first fixed byte anywhere in the pattern.
        var anchor = 0;
        var anchorLength = 0;
        while (anchorLength < mask.Length && mask[anchorLength] == 1)
            anchorLength++;
        if (anchorLength == 0)
        {
            anchor = -1;
            for (var i = 0; i < mask.Length; i++)
            {
                if (mask[i] == 1)
                {
                    anchor = i;
                    anchorLength = 1;
                    break;
                }
            }

            if (anchor < 0)
                throw new ArgumentException("Pattern must contain at least one fixed byte.", nameof(pattern));
        }

        // A match starting at s needs 0 <= s and s + length <= haystack.Length,
        // so an anchor hit at p (= s + anchor) must stay within [anchor, maxP].
        // An anchor hit at p needs bytes up to p + anchorLength - 1, so the search
        // window must extend anchorLength - 1 past maxP (still within the haystack,
        // because anchor + anchorLength <= pattern length).
        var maxP = haystack.Length - values.Length + anchor;
        var anchorBytes = values.AsSpan(anchor, anchorLength);
        var searchFrom = anchor;
        while (searchFrom <= maxP && results.Count < maxResults)
        {
            var hit = haystack.Slice(searchFrom, maxP - searchFrom + anchorLength).IndexOf(anchorBytes);
            if (hit < 0)
                break;

            var start = searchFrom + hit - anchor;
            if (FullMatch(haystack.Slice(start, values.Length), values, mask))
                results.Add(haystackBaseRva + start);

            searchFrom += hit + 1;
        }

        return results;
    }

    /// <summary>Returns the single match RVA, or throws when the count is not exactly 1.</summary>
    /// <exception cref="SignatureResolutionException">Zero or more than one match.</exception>
    public static long FindUnique(ReadOnlySpan<byte> haystack, long haystackBaseRva, NativePattern pattern)
    {
        var matches = FindAll(haystack, haystackBaseRva, pattern, maxResults: 2);
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new SignatureResolutionException("Pattern matched 0 times; expected exactly 1.", 0),
            _ => throw new SignatureResolutionException(
                $"Pattern matched at least {matches.Count} times; expected exactly 1.", matches.Count),
        };
    }

    private static bool FullMatch(ReadOnlySpan<byte> candidate, byte[] values, byte[] mask)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (mask[i] == 1 && candidate[i] != values[i])
                return false;
        }

        return true;
    }
}
