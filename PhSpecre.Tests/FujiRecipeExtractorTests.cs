using PhSpectre.Recipes;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using Xunit;

namespace PhSpecre.Tests;

public class FujiRecipeExtractorTests
{
    private static readonly FujiRecipeExtractor Extractor = new();

    // Hand-builds a byte-for-byte valid Fuji MakerNote block: "FUJIFILM" signature + u32 LE
    // sub-IFD offset (always 12, immediately after the header) + entry count + 12-byte
    // entries, with any value over 4 bytes appended after the entry table and addressed by
    // an offset relative to the MakerNote block's own start (byte 0) — the scheme confirmed
    // in FujiRecipeExtractor.cs's header comment, both against 3 real Fuji JPEGs and against
    // ExifTool's own source.
    private static byte[] BuildMakerNote(params (ushort Tag, ushort Type, uint Count, byte[] Value)[] entries)
    {
        const int headerSize = 12;
        int entryTableSize = 2 + entries.Length * 12;
        int overflowStart = headerSize + entryTableSize;

        var offsets = new int[entries.Length];
        int cursor = overflowStart;
        for (int i = 0; i < entries.Length; i++)
        {
            offsets[i] = cursor;
            if (entries[i].Value.Length > 4) cursor += entries[i].Value.Length;
        }

        using var ms = new MemoryStream();
        void U16(ushort v) => ms.Write(BitConverter.GetBytes(v));
        void U32(uint v) => ms.Write(BitConverter.GetBytes(v));

        ms.Write("FUJIFILM"u8);
        U32(headerSize);

        U16((ushort)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            var (tag, type, count, value) = entries[i];
            U16(tag);
            U16(type);
            U32(count);
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
        foreach (var (_, _, _, value) in entries)
            if (value.Length > 4)
                ms.Write(value);

        return ms.ToArray();
    }

    private static byte[] S32(int v) => BitConverter.GetBytes(v);
    private static byte[] U16Bytes(int v) => BitConverter.GetBytes((ushort)v);

    private static ExifProfile BuildProfile(string make, byte[]? makerNote, string model = "X-T5")
    {
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Make, make);
        profile.SetValue(ExifTag.Model, model);
        if (makerNote != null)
            profile.SetValue(ExifTag.MakerNote, makerNote);
        return profile;
    }

    [Fact]
    public void CanHandle_FujifilmMake_ReturnsTrue()
    {
        var profile = BuildProfile("FUJIFILM", makerNote: null);
        Assert.True(Extractor.CanHandle(profile));
    }

    [Fact]
    public void CanHandle_NonFujiMake_ReturnsFalse()
    {
        var profile = BuildProfile("Canon", makerNote: null);
        Assert.False(Extractor.CanHandle(profile));
    }

    [Fact]
    public void Extract_NonFujiMake_ReturnsNull()
    {
        var profile = BuildProfile("Canon", makerNote: null);
        Assert.Null(Extractor.Extract(profile));
    }

    [Fact]
    public void Extract_NoMakerNoteTag_ReturnsNull()
    {
        var profile = BuildProfile("FUJIFILM", makerNote: null);
        Assert.Null(Extractor.Extract(profile));
    }

    [Fact]
    public void Extract_FullKnownTagSet_DecodesEveryField()
    {
        var mn = BuildMakerNote(
            (0x1401, 3, 1, U16Bytes(0x0000)),      // FilmSimulation -> Provia/Standard
            (0x1002, 3, 1, U16Bytes(0x0000)),      // WhiteBalance -> Auto
            (0x100a, 9, 2, [.. S32(40), .. S32(-60)]), // WhiteBalanceShift raw (40, -60) -> /20 -> (2, -3)
            (0x1402, 3, 1, U16Bytes(0x0100)),      // DynamicRange (fine) -> Standard (100%)
            (0x1041, 9, 1, S32(-32)),              // HighlightTone raw -32 -> dial +2
            (0x1040, 9, 1, S32(16)),               // ShadowTone raw 16 -> dial -1
            (0x1003, 3, 1, U16Bytes(0x80)),        // Color raw 0x80 -> dial +1
            (0x1001, 3, 1, U16Bytes(0x2)),         // Sharpness raw 0x2 -> dial -2
            (0x100e, 3, 1, U16Bytes(0x100)),       // NoiseReduction raw 0x100 -> dial +2
            (0x100f, 9, 1, S32(2500)),             // Clarity raw 2500 -> 2.5m
            (0x1047, 9, 1, S32(32)),               // GrainEffect -> Weak
            (0x1048, 9, 1, S32(64)),               // ColorChromeEffect -> Strong
            (0x104e, 9, 1, S32(0)),                // ColorChromeFxBlue -> Off
            (0x1049, 6, 1, [unchecked((byte)(sbyte)-5)]) // BwAdjustment -> -5
        );
        var profile = BuildProfile("FUJIFILM", mn, model: "X-T5");

        var recipe = Extractor.Extract(profile);

        Assert.NotNull(recipe);
        Assert.Equal(PhSpectre.Models.RecipeSource.AutoDetected, recipe!.Source);
        Assert.Null(recipe.RecipeName);
        Assert.Equal("FUJIFILM", recipe.CameraMake);
        Assert.Equal("X-T5", recipe.CameraModel);
        Assert.Equal("Provia/Standard", recipe.FilmSimulation);
        Assert.Equal("Auto", recipe.WhiteBalance);
        Assert.Equal((2, -3), recipe.WhiteBalanceShift);
        Assert.Equal("Standard (100%)", recipe.DynamicRange);
        Assert.Equal(2m, recipe.HighlightTone);
        Assert.Equal(-1m, recipe.ShadowTone);
        Assert.Equal(1, recipe.Color);
        Assert.Equal(-2, recipe.Sharpness);
        Assert.Equal(2, recipe.NoiseReduction);
        Assert.Equal(2.5m, recipe.Clarity);
        Assert.Equal("Weak", recipe.GrainEffect);
        Assert.Equal("Strong", recipe.ColorChromeEffect);
        Assert.Equal("Off", recipe.ColorChromeFxBlue);
        Assert.Equal(-5, recipe.BwAdjustment);
    }

    [Theory]
    [InlineData(80, -100, 4, -5)]     // exact /20, in range
    [InlineData(0, 0, 0, 0)]          // zero shift
    [InlineData(-180, 180, -9, 9)]    // exact /20, at the ±9 boundary
    public void Extract_WhiteBalanceShift_DividesRawByTwenty(int rawRed, int rawBlue, int expectedRed, int expectedBlue)
    {
        var mn = BuildMakerNote((0x100a, 9, 2, [.. S32(rawRed), .. S32(rawBlue)]));
        var profile = BuildProfile("FUJIFILM", mn);

        var recipe = Extractor.Extract(profile);

        Assert.NotNull(recipe);
        Assert.Equal((expectedRed, expectedBlue), recipe!.WhiteBalanceShift);
    }

    [Fact]
    public void Extract_WhiteBalanceShift_NotDivisibleByTwenty_ShowsRawValueAsIs()
    {
        // 15 isn't a multiple of 20 — must come through unchanged, not rounded to 1.
        var mn = BuildMakerNote((0x100a, 9, 2, [.. S32(15), .. S32(0)]));
        var profile = BuildProfile("FUJIFILM", mn);

        var recipe = Extractor.Extract(profile);

        Assert.NotNull(recipe);
        Assert.Equal((15, 0), recipe!.WhiteBalanceShift);
    }

    [Fact]
    public void Extract_WhiteBalanceShift_DividedResultOutOfRange_ShowsRawValueAsIs()
    {
        // 400/20 = 20, outside the real ±9 dial range — must come through as the raw 400,
        // not a silently clamped ±9.
        var mn = BuildMakerNote((0x100a, 9, 2, [.. S32(400), .. S32(0)]));
        var profile = BuildProfile("FUJIFILM", mn);

        var recipe = Extractor.Extract(profile);

        Assert.NotNull(recipe);
        Assert.Equal((400, 0), recipe!.WhiteBalanceShift);
    }

    // Regression, byte-for-byte from a real file: an X-T5 (firmware Ver4.31) JPEG shot in Reala
    // ACE with a custom recipe reported ShadowTone raw 32 (-> dial -2, already correct under the
    // old code since 32 is a whole multiple of 16) but HighlightTone raw 24 — not a multiple of
    // 16 at all, only of 8. The old code's DecodeLevel fell back to returning that raw MakerNote
    // number completely unconverted, so the app displayed "+24" for what ExifTool's own "-raw/16"
    // formula (applied without rounding, matching its 12.68 "decimal values" support for exactly
    // these two tags) resolves to the real, in-range dial position -1.5.
    [Fact]
    public void Extract_RealRealaAceFile_HighlightToneHalfStep_DecodesAsMinusOnePointFive()
    {
        var mn = BuildMakerNote(
            (0x1401, 3, 1, U16Bytes(0x0b00)), // FilmSimulation -> Reala ACE
            (0x1041, 9, 1, S32(24)),          // HighlightTone raw 24 -> dial -1.5
            (0x1040, 9, 1, S32(32)));         // ShadowTone raw 32 -> dial -2
        var profile = BuildProfile("FUJIFILM", mn, model: "X-T5");

        var recipe = Extractor.Extract(profile);

        Assert.NotNull(recipe);
        Assert.Equal("Reala ACE", recipe!.FilmSimulation);
        Assert.Equal(-1.5m, recipe.HighlightTone);
        Assert.Equal(-2m, recipe.ShadowTone);
    }

    // Beyond the confirmed real half-step case above: any other raw value outside the old
    // hardcoded -64..32 table (e.g. a wider whole-step tone-dial range on newer bodies) must
    // still be divided by 16, not returned as the untouched raw MakerNote number.
    [Theory]
    [InlineData(-80, 5)]   // beyond the old table's +4 ceiling
    [InlineData(-96, 6)]
    [InlineData(48, -3)]   // beyond the old table's -2 floor
    [InlineData(64, -4)]
    public void Extract_ToneRawValueOutsideOldTable_StillDividesBySixteen(int raw, decimal expectedDial)
    {
        var mn = BuildMakerNote((0x1041, 9, 1, S32(raw)), (0x1040, 9, 1, S32(raw)));
        var profile = BuildProfile("FUJIFILM", mn);

        var recipe = Extractor.Extract(profile);

        Assert.NotNull(recipe);
        Assert.Equal(expectedDial, recipe!.HighlightTone);
        Assert.Equal(expectedDial, recipe.ShadowTone);
    }

    [Fact]
    public void Extract_ToneRawValueNotDivisibleByEight_ShowsRawValueAsIs()
    {
        // 10 isn't a multiple of 8 — the finest granularity confirmed in real MakerNote data —
        // so it must come through unchanged rather than being coerced into a dial value.
        var mn = BuildMakerNote((0x1041, 9, 1, S32(-10)));
        var profile = BuildProfile("FUJIFILM", mn);

        var recipe = Extractor.Extract(profile);

        Assert.NotNull(recipe);
        Assert.Equal(-10m, recipe!.HighlightTone);
    }

    [Fact]
    public void Extract_ToneRawValueDividedResultOutOfRange_ShowsRawValueAsIs()
    {
        // -384/16 = 24, outside the sane ±10 dial range — must come through as the raw -384,
        // not a wildly implausible +24 dial position.
        var mn = BuildMakerNote((0x1041, 9, 1, S32(-384)));
        var profile = BuildProfile("FUJIFILM", mn);

        var recipe = Extractor.Extract(profile);

        Assert.NotNull(recipe);
        Assert.Equal(-384m, recipe!.HighlightTone);
    }

    [Theory]
    [InlineData(0x300, "Monochrome")]
    [InlineData(0x302, "Monochrome + Ye Filter")]
    [InlineData(0x310, "Sepia")]
    [InlineData(0x500, "Acros")]
    [InlineData(0x503, "Acros + G Filter")]
    public void Extract_MonochromeFamilySaturation_OverridesFilmSimulation(int saturationRaw, string expectedSim)
    {
        // FilmMode (0x1401) reports a color simulation (Velvia here), but Saturation (0x1003)
        // carries a monochrome-family code — the monochrome name must win, since that's what
        // the camera actually shot and what its own UI would show.
        var mn = BuildMakerNote(
            (0x1401, 3, 1, U16Bytes(0x200)),
            (0x1003, 3, 1, U16Bytes(saturationRaw)));
        var profile = BuildProfile("FUJIFILM", mn);

        var recipe = Extractor.Extract(profile);

        Assert.NotNull(recipe);
        Assert.Equal(expectedSim, recipe!.FilmSimulation);
    }

    [Fact]
    public void Extract_NumericSaturation_DecodesColorLevelNotRawByte()
    {
        var mn = BuildMakerNote((0x1003, 3, 1, U16Bytes(0xe0))); // +4 highest
        var profile = BuildProfile("FUJIFILM", mn);

        var recipe = Extractor.Extract(profile);

        Assert.NotNull(recipe);
        Assert.Equal(4, recipe!.Color);
        Assert.Null(recipe.FilmSimulation); // no FilmMode tag present, and 0xe0 isn't mono-family
    }

    [Fact]
    public void Extract_UnmappedEnumValue_ReturnsUnknownStringInsteadOfThrowing()
    {
        var mn = BuildMakerNote((0x1002, 3, 1, U16Bytes(0x9999))); // WhiteBalance, unmapped raw value
        var profile = BuildProfile("FUJIFILM", mn);

        var recipe = Extractor.Extract(profile);

        Assert.NotNull(recipe);
        Assert.Equal("Unknown (0x9999)", recipe!.WhiteBalance);
    }

    [Fact]
    public void Extract_MissingTag_LeavesFieldNull()
    {
        var mn = BuildMakerNote((0x1002, 3, 1, U16Bytes(0x0000))); // only WhiteBalance present
        var profile = BuildProfile("FUJIFILM", mn);

        var recipe = Extractor.Extract(profile);

        Assert.NotNull(recipe);
        Assert.Null(recipe!.FilmSimulation);
        Assert.Null(recipe.BwAdjustment);
        Assert.Null(recipe.Clarity);
    }

    [Fact]
    public void Extract_TooShortMakerNote_ReturnsNullNotThrows()
    {
        var profile = BuildProfile("FUJIFILM", makerNote: [1, 2, 3]);
        Assert.Null(Extractor.Extract(profile));
    }

    [Fact]
    public void Extract_BadSignature_ReturnsNullNotThrows()
    {
        var bogus = new byte[32];
        "NOTFUJI!"u8.CopyTo(bogus);
        var profile = BuildProfile("FUJIFILM", bogus);
        Assert.Null(Extractor.Extract(profile));
    }

    [Fact]
    public void ParseFujiIfd_EntryWithOutOfBoundsOffset_SkipsThatEntryOnly()
    {
        // One well-formed inline entry (WhiteBalance) plus one entry whose value offset
        // points past the end of the buffer — the corrupt one must be dropped silently, the
        // good one must still come through.
        var mn = BuildMakerNote(
            (0x1002, 3, 1, U16Bytes(0x0000)),
            (0x1401, 2, 200, new byte[200])); // ASCII, 200 bytes -> forces an out-of-line offset
        // Truncate the buffer so the second entry's resolved offset falls outside it.
        var truncated = mn[..(mn.Length - 150)];

        var entries = FujiRecipeExtractor.ParseFujiIfd(truncated);

        Assert.NotNull(entries);
        Assert.True(entries!.ContainsKey(0x1002));
        Assert.False(entries.ContainsKey(0x1401));
    }

    [Fact]
    public void ParseFujiIfd_TruncatedEntryTable_ReturnsPartialResultsNotNull()
    {
        var mn = BuildMakerNote(
            (0x1002, 3, 1, U16Bytes(0x0000)),
            (0x1003, 3, 1, U16Bytes(128)));
        var truncated = mn[..(mn.Length - 8)]; // cuts off mid-second-entry

        var entries = FujiRecipeExtractor.ParseFujiIfd(truncated);

        Assert.NotNull(entries);
        Assert.True(entries!.ContainsKey(0x1002));
    }
}
