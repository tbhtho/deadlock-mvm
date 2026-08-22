namespace DeadlockMVM.Core.Native;

/// <summary>
/// Identifies a specific build of a PE module: PE header fields plus file length.
/// Compared at attach time so stale addresses are never used against a changed build.
/// </summary>
public sealed record ModuleFingerprint(uint TimeDateStamp, uint CheckSum, uint SizeOfImage, long FileLength)
{
    public bool Matches(ModuleFingerprint other)
        => other is not null &&
           TimeDateStamp == other.TimeDateStamp &&
           CheckSum == other.CheckSum &&
           SizeOfImage == other.SizeOfImage &&
           FileLength == other.FileLength;
}
