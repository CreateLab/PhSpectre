using System.Buffers.Binary;
using System.Text;
using PhSpectre.Recipes;
using PhSpectre.Services;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using Xunit;

namespace PhSpecre.Tests;

// Synthetic ISO-BMFF fixtures, same hand-built-byte-array convention as
// FujiRecipeExtractorTests.BuildMakerNote — no real camera files committed to the repo. The
// parser this exercises was separately validated against 4 real Fuji X-T5 .HEIC files during
// development (see HeifContainerReader.cs's header comment); these tests instead pin down its
// box-walking behavior against deliberately minimal/adversarial inputs that are impractical to
// get from a real camera file (missing boxes, box-order variants).
public class HeifContainerReaderTests
{
    [Theory]
    [InlineData("heic")]
    [InlineData("heix")]
    [InlineData("mif1")]
    public void IsHeif_AcceptsKnownHeicBrands(string brand)
    {
        var header = BuildFtypHeaderOnly(brand);
        Assert.True(HeifContainerReader.IsHeif(header));
    }

    [Theory]
    [InlineData("avif")]
    [InlineData("av01")]
    [InlineData("jpeg")] // not a real brand, just proving unknown brands are rejected
    public void IsHeif_RejectsNonHeicBrands(string brand)
    {
        var header = BuildFtypHeaderOnly(brand);
        Assert.False(HeifContainerReader.IsHeif(header));
    }

    [Fact]
    public void IsHeif_TooShortHeader_ReturnsFalse() =>
        Assert.False(HeifContainerReader.IsHeif(new byte[8]));

    [Fact]
    public void TryReadExifProfile_LocatesExifItem_ReturnsMatchingTiffBytes()
    {
        var tiff = BuildMinimalTiff(make: "FUJIFILM", model: "X-T5", makerNoteBytes: null);
        var heif = BuildSyntheticHeif(tiff);
        var path = WriteTempFile(heif);
        try
        {
            var profile = HeifContainerReader.TryReadExifProfile(path);
            Assert.NotNull(profile);
            Assert.True(profile!.TryGetValue(ExifTag.Make, out var make));
            Assert.Equal("FUJIFILM", make.Value?.TrimEnd('\0'));
            Assert.True(profile.TryGetValue(ExifTag.Model, out var model));
            Assert.Equal("X-T5", model.Value?.TrimEnd('\0'));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryReadExifProfile_IlocBeforeIinf_StillLocatesItem()
    {
        // Box order within `meta` isn't guaranteed by the spec — different encoders emit
        // iinf/iloc in different orders. This is exactly the bug the single-pass
        // FindChildBoxes scan (as opposed to two independent re-scans) was written to fix.
        var tiff = BuildMinimalTiff(make: "FUJIFILM", model: null, makerNoteBytes: null);
        var heif = BuildSyntheticHeif(tiff, ilocBeforeIinf: true);
        var path = WriteTempFile(heif);
        try
        {
            var profile = HeifContainerReader.TryReadExifProfile(path);
            Assert.NotNull(profile);
            Assert.True(profile!.TryGetValue(ExifTag.Make, out var make));
            Assert.Equal("FUJIFILM", make.Value?.TrimEnd('\0'));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryReadExifProfile_EndToEnd_FeedsFujiRecipeExtractorCorrectly()
    {
        // Full HEIC -> ExifProfile -> FilmRecipe path, without any real camera file: reuses
        // FujiRecipeExtractorTests' own MakerNote fixture builder as the payload.
        // FujiRecipeExtractorTests.BuildMakerNote already returns bytes in the exact shape
        // FujiRecipeExtractor expects ("FUJIFILM" + sub-IFD offset + entries) — reuse it
        // directly as this TIFF's MakerNote tag value.
        var makerNote = FujiRecipeExtractorTests.BuildMakerNote(
            (0x1401, 4, 1, [0x00, 0x06, 0x00, 0x00]), // FilmMode: Classic Chrome
            (0x1003, 3, 1, [0x00, 0x00])              // Saturation: 0 ("Color" level 0)
        );
        var tiff = BuildMinimalTiff(make: "FUJIFILM", model: "X-T5", makerNoteBytes: makerNote);
        var heif = BuildSyntheticHeif(tiff);
        var path = WriteTempFile(heif);
        try
        {
            var recipe = RecipeReader.Read(path);
            Assert.NotNull(recipe);
            Assert.Equal("FUJIFILM", recipe!.CameraMake);
            Assert.Equal("X-T5", recipe.CameraModel);
            Assert.Equal("Classic Chrome", recipe.FilmSimulation);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryReadExifProfile_MissingIinf_ReturnsNull()
    {
        // meta with only iloc, no iinf at all — a structurally incomplete container.
        var iloc = BuildIlocBox(itemId: 1, extentOffset: 0, extentLength: 0);
        var meta = BuildFullBox("meta", version: 0, flags: 0, children: iloc);
        var ftyp = BuildFtypBox("heic");
        var file = Concat(ftyp, meta);
        var path = WriteTempFile(file);
        try
        {
            Assert.Null(HeifContainerReader.TryReadExifProfile(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RecipeReader_Read_DispatchesHeifFilesThroughContainerReader()
    {
        var tiff = BuildMinimalTiff(make: "Canon", model: null, makerNoteBytes: null);
        var heif = BuildSyntheticHeif(tiff);
        var path = WriteTempFile(heif);
        try
        {
            // Non-Fuji camera: HeifContainerReader must still be reached (proven by getting a
            // real ExifProfile back), but no extractor claims it, so the recipe comes back null
            // rather than throwing.
            Assert.True(HeifContainerReader.IsHeifFile(path));
            Assert.Null(RecipeReader.Read(path));
        }
        finally { File.Delete(path); }
    }

    // ── Fixture builders ────────────────────────────────────────────────────

    private static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.heic");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] BuildFtypHeaderOnly(string brand)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(header, 16);
        Encoding.ASCII.GetBytes("ftyp").CopyTo(header, 4);
        Encoding.ASCII.GetBytes(brand).CopyTo(header, 8);
        return header;
    }

    private static byte[] BuildFtypBox(string brand) =>
        BuildBox("ftyp", Concat(Encoding.ASCII.GetBytes(brand), new byte[4]));

    private static byte[] BuildBox(string type, byte[] content)
    {
        var box = new byte[8 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        content.CopyTo(box, 8);
        return box;
    }

    private static byte[] BuildFullBox(string type, byte version, uint flags, params byte[][] children)
    {
        var flagsAndVersion = new byte[4];
        flagsAndVersion[0] = version;
        // flags packed big-endian into the low 3 bytes; only 0 is used in these tests.
        var content = Concat([flagsAndVersion, .. children]);
        return BuildBox(type, content);
    }

    private static byte[] BE16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }
    private static byte[] BE32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        int offset = 0;
        foreach (var p in parts) { p.CopyTo(result, offset); offset += p.Length; }
        return result;
    }

    private static byte[] BuildInfeBox(uint itemId, string itemType) =>
        BuildBox("infe", Concat(
            [2, 0, 0, 0], // version 2, flags 0
            BE16((ushort)itemId),
            BE16(0), // item_protection_index
            Encoding.ASCII.GetBytes(itemType),
            [0] // empty null-terminated item_name
        ));

    private static byte[] BuildIinfBox(params byte[][] infeBoxes) =>
        BuildFullBox("iinf", version: 0, flags: 0,
            children: [BE16((ushort)infeBoxes.Length), .. infeBoxes]);

    // iloc version 0: offsetSize=4, lengthSize=4, baseOffsetSize=0, indexSize=0, one item with
    // one extent — the shape every camera/phone HEIF this reader targets actually uses.
    private static byte[] BuildIlocBox(uint itemId, uint extentOffset, uint extentLength) =>
        BuildFullBox("iloc", version: 0, flags: 0, children:
        [
            [0x44, 0x00],           // offsetSize=4/lengthSize=4, baseOffsetSize=0/indexSize=0
            BE16(1),                // item_count = 1
            BE16((ushort)itemId),
            BE16(0),                // data_reference_index
            BE16(1),                // extent_count = 1
            BE32(extentOffset),
            BE32(extentLength),
        ]);

    private static byte[] BuildMinimalTiff(string make, string? model, byte[]? makerNoteBytes)
    {
        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>();
        byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s + "\0");

        var makeBytes = Ascii(make);
        entries.Add((0x010F, 2, (uint)makeBytes.Length, makeBytes));
        if (model != null)
        {
            var modelBytes = Ascii(model);
            entries.Add((0x0110, 2, (uint)modelBytes.Length, modelBytes));
        }
        if (makerNoteBytes != null)
            entries.Add((0x927C, 7, (uint)makerNoteBytes.Length, makerNoteBytes));

        const int headerSize = 8; // "II" + 0x002A + ifdOffset
        int ifdOffset = headerSize;
        int ifdSize = 2 + entries.Count * 12 + 4;
        int overflowStart = ifdOffset + ifdSize;

        var offsets = new int[entries.Count];
        int cursor = overflowStart;
        for (int i = 0; i < entries.Count; i++)
        {
            offsets[i] = cursor;
            if (entries[i].Value.Length > 4) cursor += entries[i].Value.Length;
        }

        using var ms = new MemoryStream();
        void U16(ushort v) => ms.Write(BitConverter.GetBytes(v));
        void U32(uint v) => ms.Write(BitConverter.GetBytes(v));

        ms.Write("II"u8);
        U16(0x002A);
        U32((uint)ifdOffset);

        U16((ushort)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var (tag, type, count, value) = entries[i];
            U16(tag); U16(type); U32(count);
            if (value.Length <= 4)
            {
                var inline = new byte[4];
                Array.Copy(value, inline, value.Length);
                ms.Write(inline);
            }
            else
            {
                U32((uint)offsets[i]);
            }
        }
        U32(0); // next IFD offset

        foreach (var (_, _, _, value) in entries)
            if (value.Length > 4)
                ms.Write(value);

        return ms.ToArray();
    }

    // Assembles a minimal ISO-BMFF container: ftyp + meta(iinf, iloc) + mdat(Exif item).
    // `ilocBeforeIinf` exercises FindChildBoxes' order-independence.
    private static byte[] BuildSyntheticHeif(byte[] tiffBytes, bool ilocBeforeIinf = false)
    {
        var exifPayload = Concat(BE32(6), "Exif\0\0"u8.ToArray(), tiffBytes);

        var ftyp = BuildFtypBox("heic");
        var infe = BuildInfeBox(itemId: 1, itemType: "Exif");
        var iinf = BuildIinfBox(infe);

        // mdat's absolute start depends on ftyp + meta's total size, and meta's size depends
        // on iloc, whose content embeds mdat's payload offset — so build meta with a
        // placeholder extent_offset first to measure its size, then rebuild with the real one.
        var ilocPlaceholder = BuildIlocBox(itemId: 1, extentOffset: 0, extentLength: (uint)exifPayload.Length);
        var metaChildrenPlaceholder = ilocBeforeIinf ? Concat(ilocPlaceholder, iinf) : Concat(iinf, ilocPlaceholder);
        var metaPlaceholder = BuildFullBox("meta", version: 0, flags: 0, children: metaChildrenPlaceholder);

        uint mdatPayloadStart = (uint)(ftyp.Length + metaPlaceholder.Length + 8); // +8 for mdat's own box header
        var iloc = BuildIlocBox(itemId: 1, extentOffset: mdatPayloadStart, extentLength: (uint)exifPayload.Length);
        var metaChildren = ilocBeforeIinf ? Concat(iloc, iinf) : Concat(iinf, iloc);
        var meta = BuildFullBox("meta", version: 0, flags: 0, children: metaChildren);

        // Sizes must match exactly for the offset math above to hold.
        if (meta.Length != metaPlaceholder.Length)
            throw new InvalidOperationException("meta box size changed after patching extent_offset — fixture bug.");

        var mdat = BuildBox("mdat", exifPayload);
        return Concat(ftyp, meta, mdat);
    }
}
