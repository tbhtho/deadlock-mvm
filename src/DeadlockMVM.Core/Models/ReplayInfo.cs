using System.Globalization;

namespace DeadlockMVM.Core.Models;

/// <summary>Metadata for one Deadlock demo file.</summary>
public sealed class ReplayInfo
{
    public string FileName { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    /// <summary>
    /// Engine-relative demo path used for playback (e.g. "replays/75438101-6448").
    /// Never a Windows filesystem path.
    /// </summary>
    public string GamePath { get; init; } = string.Empty;

    public long FileSize { get; init; }

    public DateTime ModifiedDate { get; init; }

    public string FileSizeText => FormatFileSize(FileSize);

    public string ModifiedDateText => ModifiedDate.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    private static string FormatFileSize(long bytes)
    {
        const double kilobyte = 1024;
        const double megabyte = kilobyte * 1024;
        const double gigabyte = megabyte * 1024;

        return bytes switch
        {
            >= (long)gigabyte => $"{bytes / gigabyte:0.#} GB",
            >= (long)megabyte => $"{bytes / megabyte:0.#} MB",
            >= (long)kilobyte => $"{bytes / kilobyte:0.#} KB",
            _ => $"{bytes} B",
        };
    }
}
