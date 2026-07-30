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
    CompositionGuide CompositionGuide,
    Color GutterColor,
    int GutterThickness,
    int CollageSourceMaxDimension = 2400)
{
    public string FileExtension => Format == OutputFormat.Jpeg ? ".jpg" : ".png";

    public static PaletteExportSettings SnapshotFrom(SettingsViewModel s)
    {
        var customBackground = s.UseCustomBackground ? ParseHexOrNull(s.CustomBackgroundHex) : null;
        return new(
            s.Colors, s.SamplingMode, s.ShowHex, s.HexBelow, s.MetaVerbosity, s.MetaStyle,
            s.Theme, s.ShowSwatches, s.HalfSize, s.OutputFormat, s.ExportPreset,
            s.LabelScale, s.SwatchScale, s.ShowPercent, s.SwatchShape, s.SortOrder,
            customBackground, s.CompositionGuide,
            // The collage gutter must be the exact same color as the card background it
            // sits inside (theme/custom background) — otherwise the gap between photos
            // visibly seams against the card behind it. Not a user-facing choice anymore.
            GutterColor: PaletteImageRenderer.GetBackgroundColor(s.Theme, customBackground),
            GutterThickness: s.GutterThickness,
            // Mobile-only: desktop never surfaces WorkingQuality and stays at the fixed
            // 2400 default it always had — this is purely to make mobile's "Working size"
            // dial actually control collage speed/output size, same as it already does for
            // the single-photo path (see MainViewModel.MaxWorkingDimension).
            CollageSourceMaxDimension: s.IsMobile ? s.WorkingMaxDimension : 2400);
    }

    private static Color? ParseHexOrNull(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParseHex(hex, out var c) ? c : null;
}
