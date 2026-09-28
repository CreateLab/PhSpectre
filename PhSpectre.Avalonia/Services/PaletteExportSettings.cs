using PhSpectre;
using PhSpectre.Models;
using PhSpectre.Qr;
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
    BackgroundMode BackgroundMode = BackgroundMode.Theme,
    int CollageSourceMaxDimension = 2400,
    ExportMode Mode = ExportMode.Card,
    bool ShowQrCode = false,
    QrPlacement QrPlacement = QrPlacement.Below,
    QrCaption QrCaption = QrCaption.None,
    QrContentSource QrContentSource = QrContentSource.GeneratedLink,
    QrContentPreset QrContentPreset = QrContentPreset.CameraLensPhotoInfoAndRecipe,
    string QrCustomUrl = "",
    bool QrIncludeNote = false,
    string QrNoteText = "",
    bool ShowRecipeCard = true)
{
    public string FileExtension => Format == OutputFormat.Jpeg ? ".jpg" : ".png";

    // Resolves the raw settings above into a render-ready QrRenderOptions for one photo —
    // can't be done inside SnapshotFrom itself, since the preset needs the per-photo
    // PhotoMetadata/FilmRecipe that are only known at each render call site (mirrors how
    // MainViewModel resolves recipeForRender inline right before dispatching to
    // RecipeCardRenderer.Render). Returns null when QR is off, Custom URL is blank, or a
    // Generated-link preset needs data (e.g. RecipeOnly) that isn't actually available.
    public QrRenderOptions? BuildQrRenderOptions(PaletteImageRenderer.PhotoMetadata? metadata, FilmRecipe? recipe)
    {
        if (!ShowQrCode) return null;

        // Caption is independent of Content source/preset (v4 spec §1b/§3) — a Custom URL QR
        // can still be captioned "Camera + Lens", and a fully-detailed Generated-link QR can
        // still show no caption at all.
        string? captionCamera = QrCaption != QrCaption.None ? metadata?.Camera : null;
        string? captionLens = QrCaption == QrCaption.CameraAndLens ? metadata?.Lens : null;

        if (QrContentSource == QrContentSource.CustomUrl)
        {
            return string.IsNullOrWhiteSpace(QrCustomUrl)
                ? null
                : new QrRenderOptions(QrPlacement, QrCustomUrl.Trim(), QrCaption, captionCamera, captionLens);
        }

        if (metadata == null) return null;
        if (QrContentPreset == QrContentPreset.RecipeOnly && recipe == null) return null;

        string note = QrIncludeNote ? QrNoteText : "";
        string url = QrLinkBuilder.BuildGeneratedUrl(QrContentPreset, metadata, recipe, note);
        return new QrRenderOptions(QrPlacement, url, QrCaption, captionCamera, captionLens);
    }

    // Whether k-means palette extraction should run at all for this snapshot — mirrors
    // SettingsViewModel.ComputeColors, kept in sync here so the render pipeline (which only
    // sees this record, not the live VM) can gate the expensive step without needing a
    // back-reference to Settings.
    public bool ComputeColors => Mode is ExportMode.Card or ExportMode.Collage;

    // Collage is deliberately excluded from the blurred-photo background for now — the
    // composed grid isn't "one photo" a blurred backdrop reads well against, and this is the
    // single choke point that guarantees a stray true never reaches CollageService/
    // CollageComposer regardless of what BackgroundMode the settings panel has selected.
    public bool UseBlurredBackground =>
        BackgroundMode == BackgroundMode.BlurredPhoto && Mode is not (ExportMode.Collage or ExportMode.CollageInfoOnly);

    public static PaletteExportSettings SnapshotFrom(SettingsViewModel s)
    {
        var customBackground = s.BackgroundMode == BackgroundMode.Custom ? ParseHexOrNull(s.CustomBackgroundHex) : null;
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
            BackgroundMode: s.BackgroundMode,
            // Mobile-only: desktop never surfaces WorkingQuality and stays at the fixed
            // 2400 default it always had — this is purely to make mobile's "Working size"
            // dial actually control collage speed/output size, same as it already does for
            // the single-photo path (see MainViewModel.MaxWorkingDimension).
            CollageSourceMaxDimension: s.IsMobile ? s.WorkingMaxDimension : 2400,
            Mode: s.ExportMode,
            ShowQrCode: s.ShowQrCode,
            QrPlacement: s.QrPlacement,
            QrCaption: s.QrCaption,
            QrContentSource: s.QrContentSource,
            QrContentPreset: s.QrContentPreset,
            QrCustomUrl: s.QrCustomUrl,
            QrIncludeNote: s.QrIncludeNote,
            QrNoteText: s.QrNoteText,
            ShowRecipeCard: s.ShowRecipeCard);
    }

    private static Color? ParseHexOrNull(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParseHex(hex, out var c) ? c : null;
}
