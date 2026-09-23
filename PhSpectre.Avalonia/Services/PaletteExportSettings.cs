using PhSpectre;
using PhSpectre.Models;
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
    int CollageSourceMaxDimension = 2400,
    ExportMode Mode = ExportMode.Card)
{
    public string FileExtension => Format == OutputFormat.Jpeg ? ".jpg" : ".png";

    // Whether k-means palette extraction should run at all for this snapshot — mirrors
    // SettingsViewModel.ComputeColors, kept in sync here so the render pipeline (which only
    // sees this record, not the live VM) can gate the expensive step without needing a
    // back-reference to Settings.
    public bool ComputeColors => Mode is ExportMode.Card or ExportMode.Collage;

    public static PaletteExportSettings SnapshotFrom(SettingsViewModel s)
    {
        var customBackground = s.UseCustomBackground ? ParseHexOrNull(s.CustomBackgroundHex) : null;
        // The camera-info plate only actually draws when EffectiveShowCameraInfo is true
        // (the manual toggle, or forced on for InfoOnly/CollageInfoOnly) — otherwise force
        // MetaVerbosity.Off regardless of what the (now-hidden) Metadata section last had,
        // so the toggle actually controls the rendered output and not just section visibility.
        var effectiveVerbosity = s.EffectiveShowCameraInfo ? s.MetaVerbosity : MetaVerbosity.Off;
        return new(
            s.Colors, s.SamplingMode, s.ShowHex, s.HexBelow, effectiveVerbosity, s.MetaStyle,
            s.Theme, s.ComputeColors, s.HalfSize, s.OutputFormat, s.ExportPreset,
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
            CollageSourceMaxDimension: s.IsMobile ? s.WorkingMaxDimension : 2400,
            Mode: s.ExportMode);
    }

    private static Color? ParseHexOrNull(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParseHex(hex, out var c) ? c : null;
}
