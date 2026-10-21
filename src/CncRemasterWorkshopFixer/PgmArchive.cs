using System.Text;

namespace CncRemasterWorkshopFixer;

public sealed class PgmArchive
{
    // C&C Remastered MAPDATA.PGM uses Petroglyph's MEG container format.
    // This deliberately narrow Format 3 reader references Petrolution's public
    // documentation and Iron Curtain's cnc-formats reader (MIT OR Apache-2.0).
    // No cnc-formats code is vendored or required at runtime; see README.md.
    // This reader supports only the raw C&C Remastered map-package subset of
    // Petroglyph MEG Format 3. Other MEG layouts are rejected explicitly.
    private const uint UnencryptedFormatFlags = 0xFFFFFFFF;
    private const uint FormatIdentifier = 0x3F7D70A4;
    private const int FixedHeaderSize = 24;
    private const int MemberRecordSize = 20;

    private readonly string _path;

    private PgmArchive(string path, IReadOnlyList<PgmMember> members)
    {
        _path = path;
        Members = members;
    }

    public IReadOnlyList<PgmMember> Members { get; }

    public static PgmArchive Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        if (stream.Length < FixedHeaderSize)
        {
            throw new InvalidDataException("The PGM file is too small to contain a header.");
        }

        if (reader.ReadUInt32() != UnencryptedFormatFlags)
        {
            throw new InvalidDataException("The file does not use the supported unencrypted MAPDATA.PGM format flags.");
        }

        if (reader.ReadUInt32() != FormatIdentifier)
        {
            throw new InvalidDataException("The file does not have the expected MAPDATA.PGM format marker.");
        }
        // The 24-byte header is followed by a length-prefixed filename table and then
        // one fixed 20-byte record per member. Payload offsets in those records are
        // absolute archive offsets, rather than offsets relative to dataStart.
        var dataStart = reader.ReadUInt32();
        var filenameCount = reader.ReadUInt32();
        var fileCount = reader.ReadUInt32();
        var filenameTableSize = reader.ReadUInt32();

        if (filenameCount != fileCount || fileCount == 0 || fileCount > 1_024)
        {
            throw new InvalidDataException("The PGM member counts are invalid or unsupported.");
        }

        var calculatedDataStart = checked((uint)(FixedHeaderSize + filenameTableSize + fileCount * MemberRecordSize));
        if (dataStart != calculatedDataStart || dataStart > stream.Length)
        {
            throw new InvalidDataException("The PGM data start is inconsistent with its tables.");
        }

        var names = ReadNames(reader, filenameCount, filenameTableSize);
        var members = new List<PgmMember>(checked((int)fileCount));

        for (var i = 0; i < fileCount; i++)
        {
            var flags = reader.ReadUInt16();
            _ = reader.ReadUInt32(); // filename CRC; not needed for member lookup
            _ = reader.ReadUInt32(); // file-record index; not needed for member lookup
            var size = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var nameIndex = reader.ReadUInt16();

            if (flags != 0)
            {
                throw new NotSupportedException($"PGM member {i} uses unsupported storage flags 0x{flags:X4}.");
            }

            if (nameIndex >= names.Count)
            {
                throw new InvalidDataException($"PGM member {i} has an invalid filename index.");
            }

            // Use a widened checked end calculation before allocating or seeking. This
            // prevents integer wrap and refuses records that overlap the metadata area
            // or extend beyond the physical file.
            var end = checked((ulong)offset + size);
            if (offset < dataStart || end > (ulong)stream.Length)
            {
                throw new InvalidDataException($"PGM member '{names[nameIndex]}' lies outside the archive.");
            }

            members.Add(new PgmMember(names[nameIndex], offset, size));
        }

        return new PgmArchive(Path.GetFullPath(path), members);
    }

    public byte[] Read(PgmMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (!Members.Contains(member))
        {
            throw new ArgumentException("The member does not belong to this archive.", nameof(member));
        }

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = member.Offset;
        var data = new byte[checked((int)member.Size)];
        stream.ReadExactly(data);
        return data;
    }

    private static List<string> ReadNames(BinaryReader reader, uint count, uint tableSize)
    {
        var start = reader.BaseStream.Position;
        var names = new List<string>(checked((int)count));

        for (var i = 0; i < count; i++)
        {
            var byteLength = reader.ReadUInt16();
            var bytes = reader.ReadBytes(byteLength);
            if (bytes.Length != byteLength)
            {
                throw new EndOfStreamException("The PGM filename table is truncated.");
            }

            var name = Encoding.UTF8.GetString(bytes);
            // Member directories are not part of the supported map subset. Requiring a
            // single safe filename also prevents inspection consumers from receiving a
            // traversal or rooted path.
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
            {
                throw new InvalidDataException("The PGM contains an unsafe or empty member name.");
            }

            names.Add(name);
        }

        if (reader.BaseStream.Position - start != tableSize)
        {
            throw new InvalidDataException("The PGM filename table size is inconsistent.");
        }

        return names;
    }
}

public sealed record PgmMember(
    string Name,
    uint Offset,
    uint Size);
