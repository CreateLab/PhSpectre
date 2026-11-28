using PhSpectre;
using PhSpectre.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Rendering;

// Separate from PaletteExportSettings, which lives in the UI project.
public sealed record CollageRenderSettings(
    int? Colors,
    SamplingMode SamplingMode,
    bool ShowHex,
    bool HexBelow,
    MetaVerbosity MetaVerbosity,
    MetaStyle MetaStyle,
    Theme Theme,
    bool ShowSwatches,
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
    int SourceMaxDimension = 2400,
    bool ComputeColors = true,
    QrRenderOptions? Qr = null)
{
    public string FileExtension => Format == OutputFormat.Jpeg ? ".jpg" : ".png";
}

// Composes the photos, extracts a palette weighted by displayed area and renders it.
public static class CollageService
{
    public static async Task RenderCollageAsync(
        IReadOnlyList<string> sourcePaths,
        string outputPath,
        CollageRenderSettings settings,
        CancellationToken cancellationToken,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
            throw new ArgumentException("At least one photo is required.", nameof(sourcePaths));

        // sourcePaths order is the packing order; the first photo supplies the EXIF.
        var metadata = metadataOverride ?? PaletteImageRenderer.ReadMetadata(sourcePaths[0]);

        var photos = await LoadSourcePhotosAsync(sourcePaths, settings.SourceMaxDimension, cancellationToken);
        try
        {
            double targetAspect = ExportAspect(settings.ExportPreset);
            var collageOptions = new CollageOptions(settings.GutterColor, settings.GutterThickness, targetAspect, settings.SourceMaxDimension);
            var composed = CollageComposer.Compose(photos, collageOptions);

            try
            {
                Models.ColorPalette palette;
                if (settings.ComputeColors)
                {
                    var weightedPhotos = photos
                        .Zip(composed.AreaWeights, (img, weight) => (Image: img, Weight: weight))
                        .ToList();

                    palette = await new PaletteExtractor().ExtractWeightedAsync(
                        weightedPhotos, settings.Colors, settings.SamplingMode, cancellationToken);
                }
                else
                {
                    // This placeholder is never drawn.
                    palette = new Models.ColorPalette([new Models.ColorSwatch("#000000", (0, 0, 0), 1f)]);
                }

                cancellationToken.ThrowIfCancellationRequested();

                PaletteImageRenderer.Render(
                    composed.Image, palette, outputPath,
                    showHex:          settings.ShowHex,
                    metaVerbosity:    settings.MetaVerbosity,
                    metaStyle:        settings.MetaStyle,
                    theme:            settings.Theme,
                    hexBelow:         settings.HexBelow,
                    showSwatches:     settings.ShowSwatches,
                    format:           settings.Format,
                    exportPreset:     settings.ExportPreset,
                    metadataOverride: metadata,
                    labelScale:       settings.LabelScale,
                    swatchScale:      settings.SwatchScale,
                    showPercent:      settings.ShowPercent,
                    swatchShape:      settings.SwatchShape,
                    sortOrder:        settings.SortOrder,
                    customBackground: settings.CustomBackground,
                    compositionGuide: settings.CompositionGuide,
                    qr:               settings.Qr);
            }
            finally
            {
                composed.Image.Dispose();
            }
        }
        finally
        {
            foreach (var photo in photos) photo.Dispose();
        }
    }

    // Layout-only preview. The caller disposes the result.
    public static async Task<Image<Rgb24>> ComposePreviewAsync(
        IReadOnlyList<string> sourcePaths,
        Color gutterColor,
        int gutterThickness,
        ExportPreset exportPreset,
        int sourceMaxDimension,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
            throw new ArgumentException("At least one photo is required.", nameof(sourcePaths));

        var photos = await LoadSourcePhotosAsync(sourcePaths, sourceMaxDimension, cancellationToken);
        try
        {
            double targetAspect = ExportAspect(exportPreset);
            var collageOptions = new CollageOptions(gutterColor, gutterThickness, targetAspect, sourceMaxDimension);
            return await Task.Run(() => CollageComposer.Compose(photos, collageOptions).Image, cancellationToken);
        }
        finally
        {
            foreach (var photo in photos) photo.Dispose();
        }
    }

    // Sources are decoded in parallel and downscaled: full-size serial decoding made large collages very slow.
    private static async Task<Image<Rgb24>[]> LoadSourcePhotosAsync(IReadOnlyList<string> sourcePaths, int sourceMaxDimension, CancellationToken cancellationToken)
    {
        var loads = sourcePaths
            .Select(path => Task.Run(() => ImageLoader.LoadWorkingCopy(path, sourceMaxDimension), cancellationToken))
            .ToArray();
        try
        {
            return await Task.WhenAll(loads);
        }
        catch
        {
            foreach (var load in loads)
                if (load.IsCompletedSuccessfully) load.Result.Dispose();
            throw;
        }
    }

    // Same preset sizes as PaletteImageRenderer; Original becomes a square.
    private static double ExportAspect(ExportPreset preset) => preset switch
    {
        ExportPreset.Square            => 1080.0 / 1080.0,
        ExportPreset.InstagramPost     => 1080.0 / 1350.0,
        ExportPreset.Story             => 1080.0 / 1920.0,
        ExportPreset.TelegramLandscape => 1920.0 / 1080.0,
        _                              => 1.0
    };
}
