namespace DeadlockMVM.Core.Vdf;

/// <summary>
/// A parsed VDF object: an ordered collection of named values. Keys are matched
/// case-insensitively, mirroring how Valve's own tools read the format.
/// </summary>
public sealed class VdfObject
{
    private readonly Dictionary<string, VdfValue> _values = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<KeyValuePair<string, VdfValue>> Entries => _values;

    public void Add(string key, VdfValue value) => _values[key] = value;

    public bool TryGet(string key, out VdfValue value) => _values.TryGetValue(key, out value!);

    /// <summary>Returns the string value for a key, or null if missing or not a string.</summary>
    public string? GetString(string key)
    {
        return _values.TryGetValue(key, out var value) ? value.AsString : null;
    }

    /// <summary>Returns the object value for a key, or null if missing or not an object.</summary>
    public VdfObject? GetObject(string key)
    {
        return _values.TryGetValue(key, out var value) ? value.AsObject : null;
    }
}
