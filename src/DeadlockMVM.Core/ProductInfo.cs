namespace DeadlockMVM.Core;

/// <summary>Product identity derived from the version stamped into the managed assemblies.</summary>
public static class ProductInfo
{
    public const string Name = "Deadlock MVM";

    public static string Version { get; } = ReadVersion();

    public static string DisplayVersion { get; } = $"v{Version}";

    private static string ReadVersion()
    {
        var version = typeof(ProductInfo).Assembly.GetName().Version;
        return version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
