using System.Buffers.Binary;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhSpectre.Services;

// Reads just enough of the ISO-BMFF (HEIF/HEIC) container structure to locate the embedded
// "Exif" item and hand its raw TIFF/IFD bytes to ImageSharp's ExifProfile — without a full
// HEIF pixel decoder or any native/third-party dependency. Verified byte-for-byte against 4
// real Fuji X-T5 HEIC files (ftyp brand "heix"): the Exif item's bytes are laid out as
// [4-byte TIFF-header-offset]["Exif\0\0"][raw TIFF data], identical to a JPEG APP1 segment
// minus the APP1 marker — so once located, that payload needs no format-specific handling at
// all; it goes straight into ExifProfile(byte[]) and from there into FujiRecipeExtractor
// completely unchanged.
//
// Deliberately supports only the common camera/phone shape: construction_method 0 (plain
// file-offset extents, no idat/item-to-item construction) and iinf/infe version 2 or 3. Any
// structural surprise (missing box, unsupported version, truncated data) returns null rather
// than throwing, matching RecipeReader's best-effort contract.
public static class HeifContainerReader
{
    // Brand check only — never used to decide *how* to decode a file's pixels, only whether
    // this reader's Exif-item lookup applies. AVIF ("avif"/"av01") is intentionally excluded:
    // out of scope, and sharing the same ftyp/meta/iinf/iloc mechanism doesn't mean this
    // reader has been verified against real AVIF files.
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

    // Stream overload so ImageSharp decoders (which receive a Stream, not a path — see
    // HeifPixelDecoder in PhSpectre.Heif) can reuse the same parser against the exact stream
    // they were handed, without needing the original file path. Leaves the stream's position
    // wherever parsing stopped, same as every other ImageSharp decoder's Stream-consuming
    // contract; callers needing the position preserved should restore it themselves.
    public static ExifProfile? TryReadExifProfile(Stream fs)
    {
        try
        {
            using var reader = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: true);

            var metaRange = FindTopLevelBox(fs, reader, "meta");
            if (metaRange is not var (metaStart, metaEnd)) return null;

            // meta is a FullBox: 1 byte version + 3 bytes flags before its children.
            fs.Position = metaStart + 4;

            // Single forward pass over meta's children: iinf/iloc order isn't guaranteed
            // (differs across encoders), so both must be collected in one scan rather than
            // two independent re-scans that would each only look forward from wherever the
            // previous one stopped.
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

            // Per ISO/IEC 23008-12 Annex A: 4-byte big-endian offset to the TIFF header,
            // then the literal ASCII "Exif\0\0", then the TIFF/IFD data itself.
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

    // Scans top-level boxes (no FullBox header) for the first one matching `type`.
    // Returns (contentStart, contentEnd) — contentStart is right after the box header.
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

    // One forward pass over [fs.Position, end), collecting the first box matching each of
    // `types` (order-independent — unlike FindTopLevelBox, this doesn't stop at the first
    // match, so callers needing more than one sibling box get them from a single scan).
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

    // Reads a box header at the current position (32-bit size, or 64-bit "largesize" when
    // size == 1). `limit` bounds how far the declared size may reach.
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

    // iinf is itself a FullBox; skip version+flags, read entry_count (u16 for v0, u32 for
    // v>=1), then walk that many "infe" child boxes looking for item_type == "Exif".
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

    // iloc is a FullBox whose layout depends on its version (0/1/2) for item_ID/item_count
    // width and whether a construction_method/index field is present. Only construction_method
    // 0 (plain file-offset extents) is supported — the shape every camera/phone HEIF uses.
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
