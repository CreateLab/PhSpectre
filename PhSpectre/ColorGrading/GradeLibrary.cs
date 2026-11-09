using System.IO.Compression;
using System.Text.Json;

namespace PhSpectre.ColorGrading;

public enum GradeKind { Lut, Preset }

public sealed class GradeEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public GradeKind Kind { get; set; }
    public string Category { get; set; } = "";
    public bool Favorite { get; set; }
    // File name inside the library folder (never a full path, so the library can be moved).
    public string FileName { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    // Preset parameters we parsed but cannot reproduce (shown to the user at import and later).
    public List<string> Skipped { get; set; } = [];
    // Ships with the app: cannot be renamed or deleted, only favorited.
    [System.Text.Json.Serialization.JsonIgnore] public bool Bundled { get; set; }
}

// Looks that ship inside the app: read-only, stable ids; only Favorite/LastUsed are stored for them.
public sealed record BundledGrade(GradeEntry Entry, Func<byte[]> ReadBytes);

public sealed record GradeImportResult(string Source, GradeEntry? Entry, string? Error);

// User library of .cube LUTs and Lightroom .xmp presets. Lives in its own folder (kept apart from
// anything bundled with the app, so updates never touch it): <root>/luts, <root>/presets, index.json.
// Files are validated with the real parsers before being accepted.
public sealed class GradeLibrary
{
    public const long MaxFileBytes = 64L * 1024 * 1024;
    private const int MaxZipEntries = 500;

    private readonly string _root;
    private readonly object _gate = new();
    private List<GradeEntry> _entries = [];
    private readonly List<BundledGrade> _bundled = [];
    private sealed record BundledState(bool Favorite, DateTime? LastUsedUtc);

    public GradeLibrary(string root, IEnumerable<BundledGrade>? bundled = null)
    {
        _root = root;
        Directory.CreateDirectory(Path.Combine(_root, "luts"));
        Directory.CreateDirectory(Path.Combine(_root, "presets"));
        if (bundled != null)
            foreach (var b in bundled) { b.Entry.Bundled = true; _bundled.Add(b); }
        Load();
    }

    private string IndexPath => Path.Combine(_root, "index.json");
    private string BundledStatePath => Path.Combine(_root, "bundled-state.json");

    private IEnumerable<GradeEntry> All => _bundled.Select(b => b.Entry).Concat(_entries);

    public IReadOnlyList<GradeEntry> Entries { get { lock (_gate) return All.ToList(); } }

    // Only for user entries; bundled ones live inside the app, use ReadBytes.
    public string PathOf(GradeEntry e) =>
        Path.Combine(_root, e.Kind == GradeKind.Lut ? "luts" : "presets", e.FileName);

    public byte[] ReadBytes(GradeEntry e)
    {
        if (e.Bundled)
        {
            var b = _bundled.FirstOrDefault(x => x.Entry.Id == e.Id)
                    ?? throw new FileNotFoundException("Look is not bundled.");
            return b.ReadBytes();
        }
        return File.ReadAllBytes(PathOf(e));
    }

    private string ReadText(GradeEntry e) => new System.Text.UTF8Encoding(false, true).GetString(ReadBytes(e));

    public LrPreset LoadPreset(GradeEntry e) => LrPreset.Parse(ReadText(e));
    public CubeLut LoadLut(GradeEntry e) => CubeLut.Parse(ReadText(e));

    private void Load()
    {
        try
        {
            if (File.Exists(IndexPath))
                _entries = JsonSerializer.Deserialize<List<GradeEntry>>(File.ReadAllText(IndexPath)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _entries = []; // corrupt index: files stay on disk, user can re-import
        }
        _entries.RemoveAll(e => !File.Exists(PathOf(e)));

        try
        {
            if (File.Exists(BundledStatePath)
                && JsonSerializer.Deserialize<Dictionary<string, BundledState>>(File.ReadAllText(BundledStatePath)) is { } state)
                foreach (var b in _bundled)
                    if (state.TryGetValue(b.Entry.Id, out var s)) { b.Entry.Favorite = s.Favorite; b.Entry.LastUsedUtc = s.LastUsedUtc; }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }

        DropUserCopiesOfBundled();
    }

    // Copies imported before the app shipped these Looks would show up twice. Byte-identical copies
    // with the same name are folded into the bundled entry (favorite / last-used carry over).
    private void DropUserCopiesOfBundled()
    {
        if (_bundled.Count == 0 || _entries.Count == 0) return;
        bool changed = false;
        foreach (var e in _entries.ToList())
        {
            var twin = _bundled.FirstOrDefault(b => b.Entry.Kind == e.Kind && b.Entry.Name == e.Name);
            if (twin == null) continue;
            try
            {
                if (!twin.ReadBytes().AsSpan().SequenceEqual(File.ReadAllBytes(PathOf(e)))) continue;
                twin.Entry.Favorite |= e.Favorite;
                if (e.LastUsedUtc > twin.Entry.LastUsedUtc) twin.Entry.LastUsedUtc = e.LastUsedUtc;
                try { File.Delete(PathOf(e)); } catch (IOException) { }
                _entries.Remove(e);
                changed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (changed) Save();
    }

    private void Save()
    {
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, IndexPath, overwrite: true);

        if (_bundled.Count == 0) return;
        var state = _bundled.Where(b => b.Entry.Favorite || b.Entry.LastUsedUtc != null)
            .ToDictionary(b => b.Entry.Id, b => new BundledState(b.Entry.Favorite, b.Entry.LastUsedUtc));
        var tmp2 = BundledStatePath + ".tmp";
        File.WriteAllText(tmp2, JsonSerializer.Serialize(state));
        File.Move(tmp2, BundledStatePath, overwrite: true);
    }

    public GradeImportResult Import(string path, string category = "")
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new(path, null, "Файл не найден.");
            if (info.Length > MaxFileBytes) return new(path, null, "Файл слишком большой.");
            return ImportBytes(Path.GetFileName(path), File.ReadAllBytes(path), category);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(path, null, $"Не удалось прочитать файл: {ex.Message}");
        }
    }

    public IReadOnlyList<GradeImportResult> ImportMany(IEnumerable<string> paths, string category = "")
    {
        var results = new List<GradeImportResult>();
        foreach (var p in paths)
            results.AddRange(p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ImportZip(p, category) : [Import(p, category)]);
        return results;
    }

    public IReadOnlyList<GradeImportResult> ImportZip(string zipPath, string category = "")
    {
        var results = new List<GradeImportResult>();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            int n = 0;
            foreach (var e in zip.Entries)
            {
                var ext = Path.GetExtension(e.Name).ToLowerInvariant();
                if (ext is not (".cube" or ".xmp")) continue;
                if (++n > MaxZipEntries) { results.Add(new(zipPath, null, "В архиве слишком много файлов.")); break; }
                if (e.Length > MaxFileBytes) { results.Add(new(e.Name, null, "Файл слишком большой.")); continue; }
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                // Only the leaf name is used: entry paths are never written to disk (zip-slip safe).
                results.Add(ImportBytes(e.Name, ms.ToArray(), category));
            }
            if (n == 0) results.Add(new(zipPath, null, "В архиве нет файлов .cube или .xmp."));
        }
        catch (InvalidDataException)
        {
            results.Add(new(zipPath, null, "Архив повреждён."));
        }
        catch (IOException ex)
        {
            results.Add(new(zipPath, null, $"Не удалось прочитать архив: {ex.Message}"));
        }
        return results;
    }

    private GradeImportResult ImportBytes(string fileName, byte[] data, string category)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        string text;
        try { text = new System.Text.UTF8Encoding(false, true).GetString(data); }
        catch (System.Text.DecoderFallbackException) { return new(fileName, null, "Файл не является текстовым (UTF-8)."); }

        GradeKind kind;
        string name = Path.GetFileNameWithoutExtension(fileName);
        var skipped = new List<string>();
        try
        {
            if (ext == ".cube")
            {
                var lut = CubeLut.Parse(text);
                kind = GradeKind.Lut;
                if (!string.IsNullOrWhiteSpace(lut.Title)) name = lut.Title!.Trim();
            }
            else if (ext == ".xmp")
            {
                var p = LrPreset.Parse(text);
                kind = GradeKind.Preset;
                if (!string.IsNullOrWhiteSpace(p.Name)) name = p.Name!.Trim();
                skipped.AddRange(p.Skipped);
            }
            else return new(fileName, null, "Поддерживаются только .cube и .xmp.");
        }
        catch (FormatException ex)
        {
            return new(fileName, null, ex.Message);
        }

        lock (_gate)
        {
            // Importing something the app already ships just returns the bundled Look.
            var twin = _bundled.FirstOrDefault(b => b.Entry.Kind == kind && b.ReadBytes().AsSpan().SequenceEqual(data));
            if (twin != null) return new(fileName, twin.Entry, null);

            var id = Guid.NewGuid().ToString("N");
            var stored = id + ext; // generated name: no collisions, no user-controlled path parts
            var entry = new GradeEntry
            {
                Id = id, Name = name, Kind = kind, Category = category, FileName = stored,
                AddedUtc = DateTime.UtcNow, Skipped = skipped
            };
            File.WriteAllBytes(PathOf(entry), data);
            _entries.Add(entry);
            Save();
            return new(fileName, entry, null);
        }
    }

    public bool Rename(string id, string name) =>
        !string.IsNullOrWhiteSpace(name) && Mutate(id, e => e.Name = name.Trim(), userOnly: true);

    public bool SetCategory(string id, string category) => Mutate(id, e => e.Category = category.Trim(), userOnly: true);
    public bool SetFavorite(string id, bool fav) => Mutate(id, e => e.Favorite = fav);
    public bool MarkUsed(string id) => Mutate(id, e => e.LastUsedUtc = DateTime.UtcNow);

    private bool Mutate(string id, Action<GradeEntry> change, bool userOnly = false)
    {
        lock (_gate)
        {
            var e = All.FirstOrDefault(x => x.Id == id);
            if (e == null || (userOnly && e.Bundled)) return false;
            change(e);
            Save();
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e == null) return false; // bundled Looks are not in _entries, so they cannot be deleted
            try { File.Delete(PathOf(e)); } catch (IOException) { }
            _entries.Remove(e);
            Save();
            return true;
        }
    }

    public IReadOnlyList<GradeEntry> Query(GradeKind? kind = null, string? search = null,
        string? category = null, bool favoritesOnly = false)
    {
        lock (_gate)
            return All
                .Where(e => (kind == null || e.Kind == kind)
                            && (!favoritesOnly || e.Favorite)
                            && (string.IsNullOrEmpty(category) || e.Category == category)
                            && (string.IsNullOrWhiteSpace(search)
                                || e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                                || e.Category.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public IReadOnlyList<GradeEntry> Recent(int count = 10)
    {
        lock (_gate)
            return All.Where(e => e.LastUsedUtc != null).OrderByDescending(e => e.LastUsedUtc).Take(count).ToList();
    }
}
