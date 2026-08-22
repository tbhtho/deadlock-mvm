using System.Text;

namespace DeadlockMVM.Core.Native;

/// <summary>One PE section header. <see cref="VirtualAddress"/> is an RVA.</summary>
public sealed record PeSection(
    string Name, long VirtualAddress, long VirtualSize, long RawOffset, long RawSize, uint Characteristics);

/// <summary>
/// Minimal, read-only PE parser over on-disk file bytes: headers, sections and the
/// build fingerprint. All offsets are bounds-checked; malformed input throws
/// <see cref="BadImageFormatException"/>.
/// </summary>
public sealed class PeImage
{
    private const ushort MzSignature = 0x5A4D;          // "MZ"
    private const uint PeSignature = 0x00004550;        // "PE\0\0"
    private const ushort Pe32Magic = 0x10B;
    private const ushort Pe32PlusMagic = 0x20B;
    private const int CoffHeaderSize = 20;
    private const int SectionHeaderSize = 40;
    private const int OptionalHeaderMinSize = 68;       // through CheckSum
    private const int SizeOfImageOffset = 56;           // within optional header (PE32 and PE32+)
    private const int CheckSumOffset = 64;

    /// <summary>Section flag IMAGE_SCN_MEM_EXECUTE.</summary>
    public const uint ImageScnMemExecute = 0x20000000;

    private PeImage(byte[] bytes, ModuleFingerprint fingerprint, IReadOnlyList<PeSection> sections)
    {
        Bytes = bytes;
        Fingerprint = fingerprint;
        Sections = sections;
    }

    public ModuleFingerprint Fingerprint { get; }

    public IReadOnlyList<PeSection> Sections { get; }

    public byte[] Bytes { get; }

    /// <exception cref="BadImageFormatException">The bytes are not a well-formed PE image.</exception>
    public static PeImage Load(byte[] fileBytes)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);

        var header = ParseImageHeader(fileBytes);
        var sectionsOffset = (long)header.SectionsOffset;
        var sectionsEnd = checked(sectionsOffset + (long)header.NumberOfSections * SectionHeaderSize);
        if (sectionsEnd > fileBytes.Length)
            throw new BadImageFormatException("Section table extends past end of file.");

        var sections = new List<PeSection>(header.NumberOfSections);
        for (var i = 0; i < header.NumberOfSections; i++)
        {
            var at = (int)(sectionsOffset + (long)i * SectionHeaderSize);
            var name = ReadSectionName(fileBytes, at);
            var virtualSize = BitConverter.ToUInt32(fileBytes, at + 8);
            var virtualAddress = BitConverter.ToUInt32(fileBytes, at + 12);
            var rawSize = BitConverter.ToUInt32(fileBytes, at + 16);
            var rawOffset = BitConverter.ToUInt32(fileBytes, at + 20);
            var characteristics = BitConverter.ToUInt32(fileBytes, at + 36);
            if (rawSize > 0 && (long)rawOffset + rawSize > fileBytes.Length)
                throw new BadImageFormatException($"Section '{name}' raw data extends past end of file.");

            sections.Add(new PeSection(name, virtualAddress, virtualSize, rawOffset, rawSize, characteristics));
        }

        return new PeImage(
            fileBytes,
            new ModuleFingerprint(header.TimeDateStamp, header.CheckSum, header.SizeOfImage, fileBytes.Length),
            sections);
    }

    /// <summary>
    /// Reads just the build fingerprint from the start of a PE image (e.g. the first
    /// page of a module in another process's memory). <paramref name="fileLength"/>
    /// is supplied by the caller since the whole file is not necessarily available.
    /// </summary>
    /// <exception cref="BadImageFormatException">The header is not a well-formed PE image.</exception>
    public static ModuleFingerprint ReadFingerprint(byte[] headerBytes, long fileLength)
    {
        ArgumentNullException.ThrowIfNull(headerBytes);
        var header = ParseImageHeader(headerBytes);
        return new ModuleFingerprint(header.TimeDateStamp, header.CheckSum, header.SizeOfImage, fileLength);
    }

    private sealed record ImageHeader(
        uint TimeDateStamp, uint CheckSum, uint SizeOfImage, int SectionsOffset, int NumberOfSections);

    private static ImageHeader ParseImageHeader(byte[] fileBytes)
    {
        if (fileBytes.Length < 0x40)
            throw new BadImageFormatException("File is too small for a DOS header.");
        if (BitConverter.ToUInt16(fileBytes, 0) != MzSignature)
            throw new BadImageFormatException("Missing MZ signature.");

        var peOffset = BitConverter.ToInt32(fileBytes, 0x3C);
        if (peOffset < 0x40 || peOffset > fileBytes.Length - (4 + CoffHeaderSize))
            throw new BadImageFormatException("e_lfanew is out of bounds.");
        if (BitConverter.ToUInt32(fileBytes, peOffset) != PeSignature)
            throw new BadImageFormatException("Missing PE signature.");

        var coff = peOffset + 4;
        var numberOfSections = BitConverter.ToUInt16(fileBytes, coff + 2);
        var timeDateStamp = BitConverter.ToUInt32(fileBytes, coff + 4);
        var sizeOfOptionalHeader = BitConverter.ToUInt16(fileBytes, coff + 16);

        var optional = coff + CoffHeaderSize;
        if (sizeOfOptionalHeader < OptionalHeaderMinSize || optional + sizeOfOptionalHeader > fileBytes.Length)
            throw new BadImageFormatException("Optional header is missing or truncated.");
        var magic = BitConverter.ToUInt16(fileBytes, optional);
        if (magic is not (Pe32Magic or Pe32PlusMagic))
            throw new BadImageFormatException($"Unknown optional-header magic 0x{magic:X4}.");

        if (numberOfSections == 0)
            throw new BadImageFormatException("PE image has no sections.");

        return new ImageHeader(
            timeDateStamp,
            BitConverter.ToUInt32(fileBytes, optional + CheckSumOffset),
            BitConverter.ToUInt32(fileBytes, optional + SizeOfImageOffset),
            optional + sizeOfOptionalHeader,
            numberOfSections);
    }

    /// <summary>
    /// Maps an RVA to a file offset via the section table.
    /// </summary>
    /// <exception cref="InvalidDataException">The RVA is not backed by file bytes.</exception>
    public long RvaToFileOffset(long rva)
    {
        if (rva < 0)
            throw new ArgumentOutOfRangeException(nameof(rva));

        foreach (var section in Sections)
        {
            var span = Math.Max(section.VirtualSize, section.RawSize);
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + span)
                continue;

            var offset = section.RawOffset + (rva - section.VirtualAddress);
            if (offset >= Bytes.Length)
                throw new InvalidDataException($"RVA 0x{rva:X} maps past end of file (section '{section.Name}').");
            return offset;
        }

        throw new InvalidDataException($"RVA 0x{rva:X} is not mapped by any section.");
    }

    /// <summary>The <c>.text</c> section, or the first executable section as a fallback.</summary>
    public PeSection? FindCodeSection()
    {
        foreach (var section in Sections)
        {
            if (string.Equals(section.Name, ".text", StringComparison.Ordinal))
                return section;
        }

        foreach (var section in Sections)
        {
            if ((section.Characteristics & ImageScnMemExecute) != 0)
                return section;
        }

        return null;
    }

    private static string ReadSectionName(byte[] fileBytes, int at)
    {
        var length = 0;
        while (length < 8 && fileBytes[at + length] != 0)
            length++;
        return Encoding.ASCII.GetString(fileBytes, at, length);
    }
}
