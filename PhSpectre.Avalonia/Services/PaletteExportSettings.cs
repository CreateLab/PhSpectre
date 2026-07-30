using PhSpectre;
using PhSpectre.Rendering;
using PhSpectre.Avalonia.ViewModels;
using SixLabors.ImageSharp;

namespace PhSpectre.Avalonia.Services;

// Immutable snapshot of the settings panel, taken once at the start of an export (single
// or batch) so the parameters used for a whole batch can't drift if the user keeps
// adjusting the live panel while it's running.
public sealed record PaletteExportSettings(
    int? Colors,
    SamplingMode SamplingMode,
    bool ShowHex,
    bool HexBelow,
    MetaVerbosity MetaVerbosity,
    MetaStyle MetaStyle,
    Theme Theme,
    bool ShowSwatches,
    bool HalfSize,
    OutputFormat Format,
    ExportPreset ExportPreset,
    float LabelScale,
    float SwatchScale,
    bool ShowPercent,
    SwatchShape SwatchShape,
    SortOrder SortOrder,
    Color? CustomBackground,
    CompositionGuide CompositionGuide)
{
    public string FileExtension => Format == OutputFormat.Jpeg ? ".jpg" : ".png";

    public static PaletteExportSettings SnapshotFrom(SettingsViewModel s) => new(
        s.Colors, s.SamplingMode, s.ShowHex, s.HexBelow, s.MetaVerbosity, s.MetaStyle,
        s.Theme, s.ShowSwatches, s.HalfSize, s.OutputFormat, s.ExportPreset,
        s.LabelScale, s.SwatchScale, s.ShowPercent, s.SwatchShape, s.SortOrder,
        s.UseCustomBackground ? ParseHexOrNull(s.CustomBackgroundHex) : null,
        s.CompositionGuide);

    private static Color? ParseHexOrNull(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParseHex(hex, out var c) ? c : null;
}
