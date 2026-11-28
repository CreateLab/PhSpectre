using PhSpectre;
using PhSpectre.Models;
using PhSpectre.Qr;
using PhSpectre.Rendering;
using PhSpectre.Avalonia.ViewModels;
using SixLabors.ImageSharp;

namespace PhSpectre.Avalonia.Services;

// Everything a rendered card depends on: the cached render is reused only while all of it is unchanged.
public sealed record CardRenderKey(string Source, PaletteExportSettings Settings,
    PaletteImageRenderer.PhotoMetadata Metadata, FilmRecipe? Recipe, string Look)
{
    public static CardRenderKey From(string source, SettingsViewModel s) => new(source,
        PaletteExportSettings.SnapshotFrom(s), s.BuildMetadataOverride(), s.DetectedRecipe, s.CardLookKey);
}

// Settings frozen at the start of an export, so a batch cannot change midway.
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

    // Resolved per photo, because a preset needs its metadata and recipe. Null when there is nothing to encode.
    public QrRenderOptions? BuildQrRenderOptions(PaletteImageRenderer.PhotoMetadata? metadata, FilmRecipe? recipe)
    {
        if (!ShowQrCode) return null;

        // The caption is independent of the content source.
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

    public bool ComputeColors => Mode is ExportMode.Card or ExportMode.Collage;

    // A collage has no blurred-photo background: there is no single photo to blur.
    public bool UseBlurredBackground =>
        BackgroundMode == BackgroundMode.BlurredPhoto && Mode is not (ExportMode.Collage or ExportMode.CollageInfoOnly);

    public static PaletteExportSettings SnapshotFrom(SettingsViewModel s)
    {
        var customBackground = s.BackgroundMode == BackgroundMode.Custom ? ParseHexOrNull(s.CustomBackgroundHex) ?? ParseHexOrNull(s.LastValidCustomHex) : null;
        // With the camera plate off, MetaVerbosity must be Off too.
        var effectiveVerbosity = s.EffectiveShowCameraInfo ? s.MetaVerbosity : MetaVerbosity.Off;
        return new(
            s.Colors, s.SamplingMode, s.ShowHex, s.HexBelow, effectiveVerbosity, s.MetaStyle,
            s.Theme, s.ComputeColors, s.HalfSize, s.OutputFormat, s.ExportPreset,
            s.LabelScale, s.SwatchScale, s.ShowPercent, s.SwatchShape, s.SortOrder,
            customBackground, s.CompositionGuide,
            // The collage gutter matches the card background, or the seams show.
            GutterColor: PaletteImageRenderer.GetBackgroundColor(s.Theme, customBackground),
            GutterThickness: s.GutterThickness,
            BackgroundMode: s.BackgroundMode,
            // Mobile only: the working size sets collage speed and output size.
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
