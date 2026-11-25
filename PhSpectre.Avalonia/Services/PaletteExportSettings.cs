using PhSpectre;
using PhSpectre.Models;
using PhSpectre.Qr;
using PhSpectre.Rendering;
using PhSpectre.Avalonia.ViewModels;
using SixLabors.ImageSharp;

namespace PhSpectre.Avalonia.Services;

// Immutable snapshot of the settings panel taken at the start of an export, so a batch can't drift while the user keeps adjusting the panel.
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
    bool ShowRecipeCard = true,
    string HiddenRecipeFields = "")
{
    public string FileExtension => Format == OutputFormat.Jpeg ? ".jpg" : ".png";

    // Resolves the raw settings into a render-ready QrRenderOptions for one photo. Done here, not in SnapshotFrom, because the preset needs the per-photo
    // PhotoMetadata/FilmRecipe known at each render call site. Returns null when QR is off, Custom URL is blank,
    // or a Generated-link preset needs data that isn't available.
    public QrRenderOptions? BuildQrRenderOptions(PaletteImageRenderer.PhotoMetadata? metadata, FilmRecipe? recipe)
    {
        if (!ShowQrCode) return null;

        // Caption is independent of the Content source/preset: a Custom URL QR can be captioned "Camera + Lens", a detailed Generated-link QR can have none.
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

    // Whether k-means extraction runs for this snapshot; mirrors SettingsViewModel.ComputeColors so the render pipeline can gate it without the live VM.
    public bool ComputeColors => Mode is ExportMode.Card or ExportMode.Collage;

    // Collage is excluded from the blurred-photo background: a composed grid isn't one photo to blur behind.
    // This is the one choke point that keeps a stray true from reaching CollageService/CollageComposer.
    public bool UseBlurredBackground =>
        BackgroundMode == BackgroundMode.BlurredPhoto && Mode is not (ExportMode.Collage or ExportMode.CollageInfoOnly);

    public static PaletteExportSettings SnapshotFrom(SettingsViewModel s)
    {
        var customBackground = s.BackgroundMode == BackgroundMode.Custom ? ParseHexOrNull(s.CustomBackgroundHex) ?? ParseHexOrNull(s.LastValidCustomHex) : null;
        // The camera-info plate draws only when EffectiveShowCameraInfo is true (the toggle, or forced for InfoOnly/CollageInfoOnly);
        // otherwise force MetaVerbosity.Off so the toggle controls the output, not just section visibility.
        var effectiveVerbosity = s.EffectiveShowCameraInfo ? s.MetaVerbosity : MetaVerbosity.Off;
        return new(
            s.Colors, s.SamplingMode, s.ShowHex, s.HexBelow, effectiveVerbosity, s.MetaStyle,
            s.Theme, s.ComputeColors, s.HalfSize, s.OutputFormat, s.ExportPreset,
            s.LabelScale, s.SwatchScale, s.ShowPercent, s.SwatchShape, s.SortOrder,
            customBackground, s.CompositionGuide,
            // The collage gutter must match the card background (theme/custom) or the gap seams against it.
            GutterColor: PaletteImageRenderer.GetBackgroundColor(s.Theme, customBackground),
            GutterThickness: s.GutterThickness,
            BackgroundMode: s.BackgroundMode,
            // Mobile only: desktop stays at the fixed 2400 default. This makes mobile's Working size control collage speed and output size
            // (see MainViewModel.MaxWorkingDimension).
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
            ShowRecipeCard: s.ShowRecipeCard,
            HiddenRecipeFields: s.HiddenRecipeFields);
    }

    private static Color? ParseHexOrNull(string? hex) =>
        HexColorInput.TryParse(hex, out var c) ? c : null;
}
