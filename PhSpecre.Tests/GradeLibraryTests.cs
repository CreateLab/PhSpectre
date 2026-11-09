using System.IO.Compression;
using PhSpectre.ColorGrading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PhSpecre.Tests;

public sealed class GradeLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phs_lib_" + Guid.NewGuid().ToString("N"));
    private readonly string _src;

    public GradeLibraryTests()
    {
        _src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(_src);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private GradeLibrary NewLib() => new(Path.Combine(_dir, "lib"));

    private const string Cube2 =
        "TITLE \"Warm\"\nLUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n";

    private const string Xmp =
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns:crs=\"http://ns.adobe.com/camera-raw-settings/1.0/\" " +
        "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:RDF>" +
        "<rdf:Description rdf:about=\"\" crs:Name=\"Moody\" crs:Exposure2012=\"0.5\" crs:Clarity2012=\"20\"/></rdf:RDF></x:xmpmeta>";

    private string Write(string name, string text)
    {
        var p = Path.Combine(_src, name);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void Import_Cube_AddsLutWithTitleName()
    {
        var lib = NewLib();
        var r = lib.Import(Write("a.cube", Cube2));
        Assert.Null(r.Error);
        Assert.Equal("Warm", r.Entry!.Name);
        Assert.Equal(GradeKind.Lut, r.Entry.Kind);
        Assert.True(File.Exists(lib.PathOf(r.Entry)));
    }

    [Fact]
    public void Import_Xmp_AddsPresetAndReportsSkipped()
    {
        var r = NewLib().Import(Write("p.xmp", Xmp));
        Assert.Null(r.Error);
        Assert.Equal(GradeKind.Preset, r.Entry!.Kind);
        Assert.Equal("Moody", r.Entry.Name);
        Assert.Contains("Clarity2012", r.Entry.Skipped);
    }

    [Fact]
    public void Import_InvalidFiles_ReturnErrorAndStoreNothing()
    {
        var lib = NewLib();
        Assert.NotNull(lib.Import(Write("bad.cube", "garbage")).Error);
        Assert.NotNull(lib.Import(Write("bad.xmp", "<a>")).Error);
        Assert.NotNull(lib.Import(Write("x.txt", "hi")).Error);
        Assert.NotNull(lib.Import(Path.Combine(_src, "missing.cube")).Error);
        Assert.Empty(lib.Entries);
    }

    [Fact]
    public void Library_PersistsAcrossInstances_AndDropsMissingFiles()
    {
        var lib = NewLib();
        var e = lib.Import(Write("a.cube", Cube2)).Entry!;
        lib.SetFavorite(e.Id, true);
        var again = NewLib();
        Assert.True(Assert.Single(again.Entries).Favorite);
        File.Delete(again.PathOf(e));
        Assert.Empty(NewLib().Entries);
    }

    [Fact]
    public void RenameDeleteFavoriteQuery_Work()
    {
        var lib = NewLib();
        var l = lib.Import(Write("a.cube", Cube2), "Кино").Entry!;
        var p = lib.Import(Write("p.xmp", Xmp)).Entry!;
        Assert.False(lib.Rename(l.Id, "  "));
        Assert.True(lib.Rename(l.Id, "Teal"));
        lib.SetFavorite(p.Id, true);
        Assert.Equal(["Teal"], lib.Query(GradeKind.Lut, "tea").Select(e => e.Name));
        Assert.Equal(["Moody"], lib.Query(favoritesOnly: true).Select(e => e.Name));
        Assert.Single(lib.Query(category: "Кино"));
        lib.MarkUsed(p.Id);
        Assert.Equal(p.Id, Assert.Single(lib.Recent()).Id);
        Assert.True(lib.Delete(l.Id));
        Assert.False(File.Exists(lib.PathOf(l)));
        Assert.False(lib.Delete(l.Id));
    }

    [Fact]
    public void ImportZip_ImportsValidSkipsOthers_AndIgnoresEntryPaths()
    {
        var zipPath = Path.Combine(_src, "pack.zip");
        using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void Add(string n, string t) { using var w = new StreamWriter(z.CreateEntry(n).Open()); w.Write(t); }
            Add("../../evil.cube", Cube2);
            Add("ok.xmp", Xmp);
            Add("bad.cube", "nope");
            Add("readme.txt", "x");
        }
        var lib = NewLib();
        var res = lib.ImportMany([zipPath]);
        Assert.Equal(2, res.Count(r => r.Error == null));
        Assert.Equal(1, res.Count(r => r.Error != null));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.cube")));
        Assert.All(lib.Entries, e => Assert.StartsWith(Path.Combine(_dir, "lib"), lib.PathOf(e)));
    }

    [Fact]
    public void ImportZip_Corrupt_ReturnsError()
    {
        var p = Write("c.zip", "not a zip");
        var r = NewLib().ImportZip(p);
        Assert.NotNull(Assert.Single(r).Error);
    }

    [Fact]
    public void GradeStack_DisabledOrEmpty_IsNoOp_AndRenderLeavesSourceUntouched()
    {
        var stack = new GradeStack { Preset = LrPreset.Parse(Xmp) };
        using var src = new Image<Rgb24>(1, 1);
        src[0, 0] = new Rgb24(60, 60, 60);
        using var graded = stack.Render(src);
        Assert.True(graded[0, 0].R > 60);
        Assert.Equal(new Rgb24(60, 60, 60), src[0, 0]);

        stack.PresetEnabled = false;
        Assert.True(stack.IsEmpty);
        using var same = stack.Render(src);
        Assert.Equal(new Rgb24(60, 60, 60), same[0, 0]);
    }

    [Fact]
    public void GradeStack_AppliesPresetThenLut()
    {
        var stack = new GradeStack { Preset = LrPreset.Parse(Xmp), Lut = CubeLut.Parse(Cube2), LutStrength = 1f };
        using var img = new Image<Rgb24>(1, 1);
        img[0, 0] = new Rgb24(60, 60, 60);
        stack.Apply(img);
        Assert.True(img[0, 0].R > 60); // identity LUT keeps the brightening from the preset
    }

    private static BundledGrade Bundled(string id, string name, GradeKind kind, string text) =>
        new(new GradeEntry { Id = id, Name = name, Kind = kind, Category = "Included" }, () => System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Bundled_AreListedLoadableAndProtected()
    {
        var lib = new GradeLibrary(Path.Combine(_dir, "lib"), [Bundled("b1", "Warm", GradeKind.Lut, Cube2), Bundled("b2", "Moody", GradeKind.Preset, Xmp)]);
        Assert.Equal(2, lib.Entries.Count);
        Assert.Single(lib.Query(GradeKind.Preset));
        Assert.NotNull(lib.LoadLut(lib.Entries.First(e => e.Id == "b1")));
        Assert.False(lib.Delete("b1"));
        Assert.False(lib.Rename("b1", "X"));
        Assert.Equal(2, lib.Entries.Count);
    }

    [Fact]
    public void Bundled_FavoriteSurvivesReload_AndImportOfSameFileReturnsBundled()
    {
        var bundled = new[] { Bundled("b1", "Warm", GradeKind.Lut, Cube2) };
        var lib = new GradeLibrary(Path.Combine(_dir, "lib"), bundled);
        lib.SetFavorite("b1", true);
        var r = lib.Import(Write("a.cube", Cube2));
        Assert.Equal("b1", r.Entry!.Id);
        Assert.Single(lib.Entries);
        var again = new GradeLibrary(Path.Combine(_dir, "lib"), [Bundled("b1", "Warm", GradeKind.Lut, Cube2)]);
        Assert.True(again.Entries.Single().Favorite);
    }

    [Fact]
    public void Bundled_FoldsEarlierIdenticalUserCopy()
    {
        var plain = new GradeLibrary(Path.Combine(_dir, "lib"));
        plain.Import(Write("a.cube", Cube2));
        var lib = new GradeLibrary(Path.Combine(_dir, "lib"), [Bundled("b1", "Warm", GradeKind.Lut, Cube2)]);
        Assert.Single(lib.Entries);
        Assert.True(lib.Entries[0].Bundled);
    }
}
