using PhSpectre;
using PhSpectre.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Rendering;

// The palette/render knobs of the single-photo export (PaletteExportSettings) plus the collage gutter settings.
// Separate from PaletteExportSettings because that type lives in the Avalonia project, which depends on this one.
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

// The collage twin of the single-photo export: composes several photos into one image, extracts a palette pooled across them
// (weighted by displayed area, so the gutter never contributes), and renders it through PaletteImageRenderer.
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

        // sourcePaths order is both the packing order and the EXIF source (the first photo represents the collage).
        // metadataOverride substitutes user-edited fields for a photo with no EXIF, as in the single-photo export.
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
                    // CollageInfoOnly: no k-means over the pooled photos. showSwatches:false below means this placeholder is never drawn.
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

    // Cheap live preview: decode and lay out the photos with a gutter, skipping palette extraction and card rendering.
    // Caller owns the returned image and must dispose it.
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

    // Loads every source already downscaled to about what the canvas can show (sourceMaxDimension ~ CollageOptions.MaxLongEdge), decoded in parallel.
    // Decoding 12-50MP photos serially at native size just to shrink them is what made an 8-photo collage take forever.
    // The cap is the caller's choice (CollageRenderSettings.SourceMaxDimension), so mobile's Working size also controls collage speed.
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

    // Mirrors the ExportPreset -> pixel mapping in PaletteImageRenderer.RenderCore, reduced to the target aspect ratio.
    // Original has no native aspect for a collage, so it falls back to a square.
    private static double ExportAspect(ExportPreset preset) => preset switch
    {
        ExportPreset.Square            => 1080.0 / 1080.0,
        ExportPreset.InstagramPost     => 1080.0 / 1350.0,
        ExportPreset.Story             => 1080.0 / 1920.0,
        ExportPreset.TelegramLandscape => 1920.0 / 1080.0,
        _                              => 1.0
    };
}
