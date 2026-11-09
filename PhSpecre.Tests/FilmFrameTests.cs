using Xunit;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpecre.Tests;

public class FilmFrameTests
{
    private static PaletteImageRenderer.PhotoMetadata Meta(string sim = "Classic Chrome", string profile = "Display P3") =>
        new("Fujifilm X-T5", "XF35mm", "35mm", "52mm eq", "f/2.8", "1/60s", "ISO 400", "2026-11-09", "-0.7 EV", "WB: Auto", "Manual", "",
            sim, profile);

    [Fact]
    public void Layout_grows_canvas_and_places_photo()
    {
        var style = new FilmStyle { SidePercent = 10, TopPercent = 5, BottomPercent = 15 };
        var (w, h, x, y) = FilmFrameRenderer.Layout(style, 1000, 800);
        Assert.Equal(80, x);               // 10% of the 800 short side
        Assert.Equal(40, y);
        Assert.Equal(1000 + 160, w);
        Assert.Equal(800 + 40 + 120, h);
    }

    [Fact]
    public void Layout_clamps_out_of_range_margins()
    {
        var style = new FilmStyle { SidePercent = 500, TopPercent = -3, BottomPercent = float.NaN };
        var (w, h, x, y) = FilmFrameRenderer.Layout(style, 1000, 1000);
        Assert.Equal(350, x);
        Assert.Equal(0, y);
        Assert.Equal(1700, w);
        Assert.Equal(1000, h);
    }

    [Fact]
    public void Empty_tokens_are_skipped_in_zone_text()
    {
        var style = new FilmStyle { Zones = { [FilmZone.BottomLeft] = [FilmToken.FilmSim, FilmToken.Iso, FilmToken.Shutter] } };
        Assert.Equal("CLASSIC CHROME  ISO 400  1/60s", FilmFrameRenderer.ZoneText(style, FilmZone.BottomLeft, Meta(), 1));
        // A non-Fuji photo has no film simulation: the zone just loses that token.
        Assert.Equal("ISO 400  1/60s", FilmFrameRenderer.ZoneText(style, FilmZone.BottomLeft, Meta(sim: ""), 1));
        Assert.Equal("", FilmFrameRenderer.ZoneText(style, FilmZone.TopCenter, Meta(), 1));
        Assert.Equal("", FilmFrameRenderer.ZoneText(style, FilmZone.BottomLeft, null, 1));
    }

    [Fact]
    public void Frame_number_tokens_print_plain_and_with_the_A_suffix()
    {
        var style = new FilmStyle { Zones = { [FilmZone.TopRight] = [FilmToken.FrameNumber], [FilmZone.BottomLeft] = [FilmToken.FrameNumberA] } };
        Assert.Equal("13", FilmFrameRenderer.ZoneText(style, FilmZone.TopRight, null, 13));
        Assert.Equal("13A", FilmFrameRenderer.ZoneText(style, FilmZone.BottomLeft, null, 13));
        Assert.Equal("", FilmFrameRenderer.ZoneText(style, FilmZone.BottomLeft, null, 0));
    }

    [Fact]
    public void Corner_zones_are_drawn_on_their_own_side_of_the_strip()
    {
        var style = FilmStyle.BuiltIns.Single(s => s.Id == "film-slide") with { Realism = 0, Dust = 0 };
        using var photo = new Image<Rgb24>(1000, 700, new Rgb24(128, 128, 128));
        using var canvas = FilmFrameRenderer.Render(photo, style, Meta(), 9, "My LUT");
        int Ink(int x0, int x1)
        {
            int n = 0;
            for (int y = 0; y < 60; y++) for (int x = x0; x < x1; x++) if (canvas[x, y] != new Rgb24(11, 11, 11)) n++;
            return n;
        }
        int third = canvas.Width / 3;
        Assert.True(Ink(0, third) > 50);               // look name, left
        Assert.True(Ink(2 * third, canvas.Width) > 20); // frame number, right
    }

    [Theory]
    [InlineData(@"C:\photos\DSCF5206.JPG", "5206")]
    [InlineData("IMG_0012.jpg", "12")]
    [InlineData("IMG_0012 (2).jpg", "2")]
    [InlineData("scan-003-final.png", "3")]
    [InlineData("sunset.jpg", "")]
    [InlineData("000.jpg", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Frame_number_is_read_from_the_file_name(string? path, string expected) =>
        Assert.Equal(expected, FilmFrameNumber.FromFileName(path));

    [Fact]
    public void Explicit_frame_label_overrides_the_position_and_empty_label_prints_nothing()
    {
        var style = new FilmStyle { Zones = { [FilmZone.TopRight] = [FilmToken.FrameNumber], [FilmZone.BottomLeft] = [FilmToken.FrameNumberA] } };
        Assert.Equal("5206", FilmFrameRenderer.ZoneText(style, FilmZone.TopRight, null, 18, null, "5206"));
        Assert.Equal("5206A", FilmFrameRenderer.ZoneText(style, FilmZone.BottomLeft, null, 18, null, "5206"));
        Assert.Equal("", FilmFrameRenderer.ZoneText(style, FilmZone.TopRight, null, 18, null, ""));
        Assert.Equal("", FilmFrameRenderer.ZoneText(style, FilmZone.BottomLeft, null, 18, null, "  "));
        Assert.Equal("18", FilmFrameRenderer.ZoneText(style, FilmZone.TopRight, null, 18));   // no label given: plain number
    }

    [Fact]
    public void Custom_token_prints_the_style_text()
    {
        var style = new FilmStyle { CustomText = "Portra 400", Zones = { [FilmZone.TopCenter] = [FilmToken.Custom] } };
        Assert.Equal("PORTRA 400", FilmFrameRenderer.ZoneText(style, FilmZone.TopCenter, null, 1));
    }

    public static IEnumerable<object[]> BuiltInIds() => FilmStyle.BuiltIns.Select(s => new object[] { s.Id });

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void Built_in_styles_render_at_layout_size_with_photo_intact(string id)
    {
        var style = FilmStyle.BuiltIns.Single(s => s.Id == id);
        using var photo = new Image<Rgb24>(600, 400, new Rgb24(200, 30, 30));
        using var result = FilmFrameRenderer.Render(photo, style, Meta(), 7);
        var (w, h, x, y) = FilmFrameRenderer.Layout(style, 600, 400);
        Assert.Equal((w, h), (result.Width, result.Height));
        // Photo pixels are untouched (only the corner arcs may be trimmed).
        Assert.Equal(new Rgb24(200, 30, 30), result[x + 300, y + 200]);
        Assert.Equal(new Rgb24(200, 30, 30), result[x + 30, y + 30]);
        Assert.Equal(new Rgb24(200, 30, 30), result[x + 570, y + 370]);
    }

    [Fact]
    public void Built_in_presets_never_print_an_invented_film_name()
    {
        Assert.Equal(FilmStyle.BuiltIns.Count, FilmStyle.BuiltIns.Select(s => s.Id).Distinct().Count());
        Assert.True(FilmStyle.BuiltIns.Count >= 8);
        // The name on the edge comes from the applied Look or the photo's film simulation, never from the preset.
        Assert.All(FilmStyle.BuiltIns, s => Assert.True(string.IsNullOrEmpty(s.CustomText), s.Id));
        Assert.All(FilmStyle.BuiltIns, s => Assert.DoesNotContain(FilmToken.Custom, s.Zones.Values.SelectMany(z => z)));
    }

    [Fact]
    public void Name_token_prefers_the_applied_look_then_the_film_simulation_then_nothing()
    {
        var style = new FilmStyle { Zones = { [FilmZone.TopCenter] = [FilmToken.FilmSim] } };
        Assert.Equal("MY LUT", FilmFrameRenderer.ZoneText(style, FilmZone.TopCenter, Meta(), 1, "My LUT"));
        Assert.Equal("CLASSIC CHROME", FilmFrameRenderer.ZoneText(style, FilmZone.TopCenter, Meta(), 1, null));
        Assert.Equal("CLASSIC CHROME", FilmFrameRenderer.ZoneText(style, FilmZone.TopCenter, Meta(), 1, "  "));
        Assert.Equal("", FilmFrameRenderer.ZoneText(style, FilmZone.TopCenter, Meta(sim: ""), 1, null));
        Assert.Equal("", FilmFrameRenderer.ZoneText(style, FilmZone.TopCenter, null, 1, null));
    }

    [Fact]
    public void Rendering_is_deterministic_for_the_same_frame_number()
    {
        var style = FilmStyle.BuiltIns.Single(s => s.Id == "film-half-amber") with { Realism = 95, Dust = 90 };   // heavy texture + dust
        using var photo = new Image<Rgb24>(300, 200, new Rgb24(90, 90, 90));
        using var a = FilmFrameRenderer.Render(photo, style, Meta(), 3);
        using var b = FilmFrameRenderer.Render(photo, style, Meta(), 3);
        using var c = FilmFrameRenderer.Render(photo, style, Meta(), 4);
        bool sameAB = true, sameAC = true;
        for (int yy = 0; yy < a.Height; yy++)
            for (int xx = 0; xx < a.Width; xx++)
            {
                if (a[xx, yy] != b[xx, yy]) sameAB = false;
                if (a[xx, yy] != c[xx, yy]) sameAC = false;
            }
        Assert.True(sameAB);      // preview and export of one frame match
        Assert.False(sameAC);     // another frame gets its own dust
    }

    [Fact]
    public void Realism_changes_the_edge_but_not_the_photo()
    {
        var plain = FilmStyle.BuiltIns.Single(s => s.Id == "film-dark") with { Realism = 0, Dust = 0, CornerPercent = 0 };
        var real = plain with { Realism = 100, Dust = 100 };
        using var photo = new Image<Rgb24>(400, 300, new Rgb24(10, 120, 200));
        using var p = FilmFrameRenderer.Render(photo, plain, null, 1);
        using var r = FilmFrameRenderer.Render(photo, real, null, 1);
        var (_, _, x, y) = FilmFrameRenderer.Layout(plain, 400, 300);
        Assert.Equal(p[x + 200, y + 150], r[x + 200, y + 150]);
        int differing = 0;
        for (int yy = 0; yy < p.Height; yy += 3) for (int xx = 0; xx < 5; xx++) if (p[xx, yy] != r[xx, yy]) differing++;
        Assert.True(differing > 20);
    }

    [Fact]
    public void Rounded_corners_trim_only_the_corner_pixels()
    {
        var style = new FilmStyle { SidePercent = 5, TopPercent = 5, BottomPercent = 5, EdgeColorHex = "#000000", CornerPercent = 4 };
        using var photo = new Image<Rgb24>(400, 400, new Rgb24(255, 255, 255));
        using var result = FilmFrameRenderer.Render(photo, style, null, 1);
        var (_, _, x, y) = FilmFrameRenderer.Layout(style, 400, 400);
        Assert.Equal(new Rgb24(0, 0, 0), result[x, y]);                       // the very corner is edge again
        Assert.Equal(new Rgb24(255, 255, 255), result[x + 200, y]);           // straight edge stays photo
        Assert.Equal(new Rgb24(255, 255, 255), result[x + 40, y + 40]);       // inside the arc stays photo
    }

    [Fact]
    public void Hole_gaps_print_each_token_between_sprockets()
    {
        var style = FilmStyle.BuiltIns.Single(s => s.Id == "film-grey");
        Assert.Equal("f/2.8", FilmFrameRenderer.ZoneText(style, FilmZone.HoleGaps, Meta(), 1));
        // The gap text really lands on the sheet: the strip carries ink that a style without gaps doesn't.
        var noGaps = style with { Zones = style.Zones.Where(z => z.Key != FilmZone.HoleGaps).ToDictionary(z => z.Key, z => z.Value), Realism = 0, Dust = 0 };
        var withGaps = style with { Realism = 0, Dust = 0 };
        using var photo = new Image<Rgb24>(800, 500, new Rgb24(128, 128, 128));
        using var a = FilmFrameRenderer.Render(photo, noGaps, Meta(), 1);
        using var b = FilmFrameRenderer.Render(photo, withGaps, Meta(), 1);
        int changed = 0;
        for (int yy = b.Height - 114; yy < b.Height; yy++) for (int xx = 0; xx < b.Width; xx++) if (a[xx, yy] != b[xx, yy]) changed++;
        Assert.True(changed > 50);
    }

    [Fact]
    public void Render_without_metadata_still_draws_the_edge()
    {
        var style = FilmStyle.BuiltIns.Single(s => s.Id == "film-cine");
        using var photo = new Image<Rgb24>(400, 600, new Rgb24(10, 200, 10));
        using var result = FilmFrameRenderer.Render(photo, style, null);
        Assert.True(result.Width > 400);
    }

    [Fact]
    public void Store_roundtrips_custom_styles_and_ignores_corrupt_file()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "film-styles.json");
        var store = new FilmStyleStore(path);
        var saved = store.Add("Mine", new FilmStyle
        {
            Glow = true, Perforation = FilmPerforation.Sides, CustomText = "HP5",
            Zones = { [FilmZone.SideLeft] = [FilmToken.Custom, FilmToken.Iso] }
        });

        var reloaded = new FilmStyleStore(path).Styles.Single();
        Assert.Equal(saved.Id, reloaded.Id);
        Assert.Equal("Mine", reloaded.Name);
        Assert.True(reloaded.Glow);
        Assert.Equal(FilmPerforation.Sides, reloaded.Perforation);
        Assert.Equal([FilmToken.Custom, FilmToken.Iso], reloaded.Zones[FilmZone.SideLeft]);

        Assert.True(store.Delete(saved.Id));
        Assert.Empty(new FilmStyleStore(path).Styles);

        File.WriteAllText(path, "{ not json");
        Assert.Empty(new FilmStyleStore(path).Styles);
    }

    [Fact]
    public void Icc_description_is_read_from_v2_desc_tag()
    {
        // 128-byte header (zeros), 1 tag 'desc' at offset 144, then a v2 'desc' block holding "sRGB".
        var d = new byte[144 + 12 + 5];
        d[131] = 1;
        "desc"u8.CopyTo(d.AsSpan(132));
        d[139] = 144;
        "desc"u8.CopyTo(d.AsSpan(144));
        d[155] = 5;
        "sRGB"u8.CopyTo(d.AsSpan(156));
        Assert.Equal("sRGB", PaletteImageRenderer.IccDescription(d));
    }

    [Fact]
    public void Film135_margins_follow_the_real_strip_whatever_the_photo_orientation()
    {
        var style = FilmStyle.BuiltIns.Single(s => s.Id == "film-dark");          // vertical strip, 135
        // Portrait 2400x3600 (24 mm across): 5.5 mm of rebate each side = 550 px, 1 mm between frames = 100 px.
        var (pw, ph, px, py) = FilmFrameRenderer.Layout(style, 2400, 3600);
        Assert.Equal((2400 + 2 * 550, 3600 + 2 * 100, 550, 100), (pw, ph, px, py));
        // Landscape: the strip is turned so the holes still run along the long side.
        var (lw, lh, lx, ly) = FilmFrameRenderer.Layout(style, 3600, 2400);
        Assert.Equal((3600 + 2 * 100, 2400 + 2 * 550), (lw, lh));
        Assert.Equal((100, 550), (lx, ly));
        using var photo = new Image<Rgb24>(360, 240, new Rgb24(200, 200, 200));
        using var framed = FilmFrameRenderer.Render(photo, style, null, 1);
        Assert.Equal((FilmFrameRenderer.Layout(style, 360, 240).Width, FilmFrameRenderer.Layout(style, 360, 240).Height), (framed.Width, framed.Height));
    }

    [Fact]
    public void Film135_hole_pitch_is_4_75_mm_on_a_24_mm_picture()
    {
        var style = FilmStyle.BuiltIns.Single(s => s.Id == "film-dark") with { Zones = new(), Realism = 0, Dust = 0, CornerPercent = 0 };
        using var photo = new Image<Rgb24>(480, 720, new Rgb24(128, 128, 128));   // 20 px per mm
        using var framed = FilmFrameRenderer.Render(photo, style, null, 1);
        int x = (int)Math.Round((2.01 + 1.981 / 2) * 20);                         // hole centre line, left strip
        var holeColor = new Rgb24(0xD9, 0xD6, 0xCE);
        int starts = 0;
        for (int y = 1; y < framed.Height; y++)
            if (framed[x, y] == holeColor && framed[x, y - 1] != holeColor) starts++;
        // 720 + 2 mm of strip = 38 mm at 4.75 mm = 8 holes, give or take the ends.
        Assert.InRange(starts, 7, 9);
    }

    [Fact]
    public void Half_frame_strip_runs_along_the_short_side()
    {
        var style = FilmStyle.BuiltIns.Single(s => s.Id == "film-half");
        // 24 x 18 mm at 100 px/mm: the 24 mm side lies across the strip (5.5 mm rebate on each side), 0.5 mm between frames.
        var (w, h, x, y) = FilmFrameRenderer.Layout(style, 2400, 1800);
        Assert.Equal((2400 + 2 * 550, 1800 + 2 * 50, 550, 50), (w, h, x, y));
        // A portrait photo is turned so the holes still sit along its short edges (top and bottom).
        var (pw, ph, px, py) = FilmFrameRenderer.Layout(style, 1800, 2400);
        Assert.Equal((1800 + 2 * 50, 2400 + 2 * 550, 50, 550), (pw, ph, px, py));
    }
}
