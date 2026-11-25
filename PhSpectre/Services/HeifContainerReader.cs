using System.Buffers.Binary;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhSpectre.Services;

// Reads just enough of the ISO-BMFF (HEIF/HEIC) container to find the "Exif" item and hand its TIFF bytes to ImageSharp's ExifProfile,
// with no pixel decoder or native dependency. Checked against 4 real Fuji X-T5 HEIC files: the item is [4-byte TIFF offset]["Exif\0\0"][TIFF data],
// i.e. a JPEG APP1 payload without the marker, so it feeds FujiRecipeExtractor unchanged.
// Only the common shape is supported: construction_method 0 (plain file-offset extents) and iinf/infe version 2 or 3.
// Any structural surprise returns null instead of throwing (best-effort, like RecipeReader).
public static class HeifContainerReader
{
    // Brand check only: decides whether the Exif-item lookup applies, not how pixels are decoded. AVIF is excluded; it shares the box structure but hasn't been verified.
    private static readonly HashSet<string> HeicBrands = new(StringComparer.Ordinal)
    {
        "heic", "heix", "heim", "heis", "hevc", "hevx", "mif1", "msf1",
    };

    public static bool IsHeif(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12) return false;
        if (!header.Slice(4, 4).SequenceEqual("ftyp"u8)) return false;
        string majorBrand = System.Text.Encoding.ASCII.GetString(header.Slice(8, 4));
        return HeicBrands.Contains(majorBrand);
    }

    public static bool IsHeifFile(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            using var fs = File.OpenRead(path);
            int read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return read == header.Length && IsHeif(header);
        }
        catch
        {
            return false;
        }
    }

    public static ExifProfile? TryReadExifProfile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return TryReadExifProfile(fs);
        }
        catch
        {
            return null;
        }
    }

    // Stream overload for ImageSharp decoders that get a Stream, not a path. Leaves the position where parsing stopped; callers restore it if needed.
    public static ExifProfile? TryReadExifProfile(Stream fs)
    {
        try
        {
            using var reader = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: true);

            var metaRange = FindTopLevelBox(fs, reader, "meta");
            if (metaRange is not var (metaStart, metaEnd)) return null;

            // meta is a FullBox: 1 byte version + 3 bytes flags before its children.
            fs.Position = metaStart + 4;

            // One forward pass over meta's children: iinf/iloc order varies by encoder, so both are collected in a single scan.
            var children = FindChildBoxes(fs, reader, metaEnd, "iinf", "iloc");
            if (!children.TryGetValue("iinf", out var iinf) || !children.TryGetValue("iloc", out var iloc))
                return null;
            var (iinfStart, iinfEnd) = iinf;
            var (ilocStart, ilocEnd) = iloc;

            fs.Position = iinfStart;
            uint? exifItemId = FindExifItemId(fs, reader, iinfEnd);
            if (exifItemId is not uint itemId) return null;

            fs.Position = ilocStart;
            var extent = FindItemExtent(fs, reader, ilocEnd, itemId);
            if (extent is not var (extentOffset, extentLength)) return null;
            if (extentLength < 6 || extentLength > 8 * 1024 * 1024) return null; // sanity bound

            fs.Position = extentOffset;
            byte[] raw = reader.ReadBytes((int)extentLength);
            if (raw.Length != extentLength) return null;

            // ISO/IEC 23008-12 Annex A: 4-byte big-endian TIFF header offset, the ASCII "Exif\0\0", then the TIFF data.
            uint tiffHeaderOffset = BinaryPrimitives.ReadUInt32BigEndian(raw);
            long tiffStart = 4L + tiffHeaderOffset;
            if (tiffStart < 6 || tiffStart >= raw.Length) return null;

            byte[] tiffBytes = raw[(int)tiffStart..];
            return new ExifProfile(tiffBytes);
        }
        catch
        {
            return null;
        }
    }

    // ── Generic box walking ─────────────────────────────────────────────────

    // Scans top-level boxes for the first of `type`; returns (contentStart, contentEnd), contentStart being right after the header.
    private static (long Start, long End)? FindTopLevelBox(Stream fs, BinaryReader reader, string type)
    {
        fs.Position = 0;
        long fileLength = fs.Length;
        while (fs.Position + 8 <= fileLength)
        {
            long boxPos = fs.Position;
            if (!TryReadBoxHeader(reader, fileLength, out long size, out string boxType, out long headerLen))
                return null;

            long contentStart = boxPos + headerLen;
            long contentEnd = boxPos + size;
            if (contentEnd > fileLength || contentEnd <= contentStart) return null;

            if (boxType == type) return (contentStart, contentEnd);
            fs.Position = contentEnd;
        }
        return null;
    }

    // One forward pass over [fs.Position, end) collecting the first box of each of `types`, in any order.
    private static Dictionary<string, (long Start, long End)> FindChildBoxes(Stream fs, BinaryReader reader, long end, params string[] types)
    {
        var found = new Dictionary<string, (long, long)>();
        var remaining = new HashSet<string>(types, StringComparer.Ordinal);

        while (remaining.Count > 0 && fs.Position + 8 <= end)
        {
            long boxPos = fs.Position;
            if (!TryReadBoxHeader(reader, end, out long size, out string boxType, out long headerLen))
                break;

            long contentStart = boxPos + headerLen;
            long contentEnd = boxPos + size;
            if (contentEnd > end || contentEnd <= contentStart) break;

            if (remaining.Remove(boxType)) found[boxType] = (contentStart, contentEnd);
            fs.Position = contentEnd;
        }
        return found;
    }

    // Reads a box header (32-bit size, or 64-bit "largesize" when size == 1); `limit` bounds the declared size.
    private static bool TryReadBoxHeader(BinaryReader reader, long limit, out long size, out string type, out long headerLen)
    {
        size = 0; type = ""; headerLen = 0;
        long start = reader.BaseStream.Position;
        if (start + 8 > limit) return false;

        uint size32 = BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
        Span<byte> typeBytes = stackalloc byte[4];
        if (reader.Read(typeBytes) != 4) return false;
        type = System.Text.Encoding.ASCII.GetString(typeBytes);

        if (size32 == 1)
        {
            if (start + 16 > limit) return false;
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(reader.ReadBytes(8));
            headerLen = 16;
        }
        else if (size32 == 0)
        {
            size = limit - start; // "extends to end of enclosing container"
            headerLen = 8;
        }
        else
        {
            size = size32;
            headerLen = 8;
        }
        return size >= headerLen;
    }

    // ── iinf / infe ──────────────────────────────────────────────────────────

    // iinf is a FullBox: skip version+flags, read entry_count (u16 for v0, u32 for v>=1), then walk the "infe" boxes for item_type == "Exif".
    private static uint? FindExifItemId(Stream fs, BinaryReader reader, long iinfEnd)
    {
        Span<byte> itemType = stackalloc byte[4];

        byte version = reader.ReadByte();
        fs.Position += 3; // flags

        uint entryCount = version == 0
            ? BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2))
            : BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));

        for (uint i = 0; i < entryCount && fs.Position + 8 <= iinfEnd; i++)
        {
            long boxPos = fs.Position;
            if (!TryReadBoxHeader(reader, iinfEnd, out long size, out string boxType, out long headerLen))
                return null;
            long contentEnd = boxPos + size;
            if (contentEnd > iinfEnd || contentEnd <= boxPos + headerLen) return null;

            if (boxType != "infe") { fs.Position = contentEnd; continue; }

            byte infeVersion = reader.ReadByte();
            fs.Position += 3; // flags

            uint itemId;
            if (infeVersion is 2)
            {
                itemId = BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));
                fs.Position += 2; // item_protection_index
            }
            else if (infeVersion is 3)
            {
                itemId = BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
                fs.Position += 2; // item_protection_index
            }
            else
            {
                fs.Position = contentEnd; // unsupported infe version — skip this entry
                continue;
            }

            if (fs.Position + 4 > contentEnd || reader.Read(itemType) != 4) return null;

            if (System.Text.Encoding.ASCII.GetString(itemType) == "Exif")
                return itemId;

            fs.Position = contentEnd;
        }
        return null;
    }

    // ── iloc ─────────────────────────────────────────────────────────────────

    // iloc is a FullBox whose item_ID/item_count width and construction_method/index fields depend on version (0/1/2).
    // Only construction_method 0 (plain file-offset extents, what cameras and phones use) is supported.
    private static (long Offset, long Length)? FindItemExtent(Stream fs, BinaryReader reader, long ilocEnd, uint targetItemId)
    {
        byte version = reader.ReadByte();
        fs.Position += 3; // flags

        byte sizesByte1 = reader.ReadByte();
        byte sizesByte2 = reader.ReadByte();
        int offsetSize = sizesByte1 >> 4;
        int lengthSize = sizesByte1 & 0xF;
        int baseOffsetSize = sizesByte2 >> 4;
        int indexSize = sizesByte2 & 0xF;

        uint itemCount = version < 2
            ? BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2))
            : BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));

        for (uint i = 0; i < itemCount && fs.Position < ilocEnd; i++)
        {
            uint itemId = version < 2
                ? BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2))
                : BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));

            int constructionMethod = 0;
            if (version is 1 or 2)
                constructionMethod = BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2)) & 0xF;

            fs.Position += 2; // data_reference_index
            long baseOffset = ReadUIntBE(reader, baseOffsetSize);
            ushort extentCount = BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));

            long? matchOffset = null, matchLength = null;
            for (int e = 0; e < extentCount; e++)
            {
                if (version is 1 or 2 && indexSize > 0)
                    fs.Position += indexSize; // extent_index, unused (no idat/item construction support)

                long extentOffset = ReadUIntBE(reader, offsetSize);
                long extentLength = ReadUIntBE(reader, lengthSize);

                if (itemId == targetItemId && matchOffset == null)
                {
                    matchOffset = baseOffset + extentOffset;
                    matchLength = extentLength;
                }
            }

            if (itemId == targetItemId && constructionMethod == 0 && matchOffset is long off && matchLength is long len)
                return (off, len);
        }
        return null;
    }

    private static long ReadUIntBE(BinaryReader reader, int byteCount) => byteCount switch
    {
        0 => 0,
        4 => BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4)),
        8 => (long)BinaryPrimitives.ReadUInt64BigEndian(reader.ReadBytes(8)),
        _ => throw new InvalidDataException($"Unsupported iloc field width: {byteCount} bytes"),
    };
}
