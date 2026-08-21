namespace DeadlockMVM.Core.Vdf;

/// <summary>A value inside a VDF object: either a string or a nested object.</summary>
public sealed class VdfValue
{
    private VdfValue()
    {
    }

    /// <summary>The string value, if this value is a string; otherwise null.</summary>
    public string? AsString { get; private init; }

    /// <summary>The nested object, if this value is an object; otherwise null.</summary>
    public VdfObject? AsObject { get; private init; }

    public bool IsString => AsString is not null;

    public bool IsObject => AsObject is not null;

    public static VdfValue FromString(string value) => new() { AsString = value };

    public static VdfValue FromObject(VdfObject value) => new() { AsObject = value };
}
