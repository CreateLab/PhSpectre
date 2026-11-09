using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PhSpectre.ColorGrading;
using global::Avalonia.Platform;

namespace PhSpectre.Avalonia.Services;

/// <summary>The presets and LUTs that ship inside the app (Assets/Looks, listed in looks.json).</summary>
internal static class BundledLooks
{
    private const string Base = "avares://PhSpectre.Avalonia/Assets/Looks/";

    private sealed class Item
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Category { get; set; } = "";
        public string File { get; set; } = "";
        public List<string> Skipped { get; set; } = [];
    }

    private static readonly Lazy<IReadOnlyList<BundledGrade>> Cache = new(Load);

    public static IReadOnlyList<BundledGrade> All => Cache.Value;

    private static IReadOnlyList<BundledGrade> Load()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri(Base + "looks.json"));
            var items = JsonSerializer.Deserialize<List<Item>>(s,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            return items.Select(i =>
            {
                var uri = new Uri(Base + i.File);
                return new BundledGrade(new GradeEntry
                {
                    Id = i.Id, Name = i.Name, Category = i.Category, Skipped = i.Skipped,
                    Kind = i.Kind == "lut" ? GradeKind.Lut : GradeKind.Preset, Bundled = true,
                }, () =>
                {
                    using var f = AssetLoader.Open(uri);
                    using var ms = new MemoryStream();
                    f.CopyTo(ms);
                    return ms.ToArray();
                });
            }).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or FileNotFoundException)
        {
            AppLogger.LogError("Failed to read the bundled looks", e);
            return [];
        }
    }
}
