using PhSpectre;
using PhSpectre.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Rendering;

// Carries the same palette/render knobs as the single-photo export flow
// (PhSpectre.Avalonia's PaletteExportSettings) plus the collage-specific gutter settings.
// Kept separate from PaletteExportSettings since that type lives in the Avalonia project,
// which depends on this one — not the other way around.
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
    int SourceMaxDimension = 2400)
{
    public string FileExtension => Format == OutputFormat.Jpeg ? ".jpg" : ".png";
}

// The collage twin of the single-photo export path: turns several source photos into one
// composed collage image, extracts a palette pooled across them (weighted by displayed
// area so the gutter itself never contributes), and renders it through the existing
// PaletteImageRenderer pipeline unchanged.
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

        // Order in sourcePaths is both the collage's packing order and the EXIF source —
        // the first photo's metadata represents the whole collage. metadataOverride lets a
        // caller substitute user-edited fields (the Settings "Edit metadata" flow) for a
        // photo that has no EXIF of its own to read, same as the single-photo export path.
        var metadata = metadataOverride ?? PaletteImageRenderer.ReadMetadata(sourcePaths[0]);

        var photos = await LoadSourcePhotosAsync(sourcePaths, settings.SourceMaxDimension, cancellationToken);
        try
        {
            double targetAspect = ExportAspect(settings.ExportPreset);
            var collageOptions = new CollageOptions(settings.GutterColor, settings.GutterThickness, targetAspect, settings.SourceMaxDimension);
            var composed = CollageComposer.Compose(photos, collageOptions);

            try
            {
                var weightedPhotos = photos
                    .Zip(composed.AreaWeights, (img, weight) => (Image: img, Weight: weight))
                    .ToList();

                var palette = await new PaletteExtractor().ExtractWeightedAsync(
                    weightedPhotos, settings.Colors, settings.SamplingMode, cancellationToken);

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
                    compositionGuide: settings.CompositionGuide);
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

    // Cheap live-preview path: just decode + lay the photos out with a gutter, skipping the
    // palette extraction (k-means over pooled samples) and card rendering entirely — that's
    // the expensive part, and the whole point of this method is to update instantly while
    // the user is still assembling the tray, before they've asked for a real render.
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

    // Loads every source photo already downscaled to roughly what the collage canvas can
    // actually show (sourceMaxDimension ≈ CollageOptions.MaxLongEdge), decoded in parallel
    // across the thread pool. Full-res mobile camera photos (12-50MP, 20MB+ JPEGs) decoded
    // serially at native resolution — just to be shrunk into a canvas cell afterwards — is
    // what made assembling an 8-photo collage take "forever"; nothing downstream ever needs
    // more detail than the final composed canvas has room for. The cap itself is a caller
    // choice (CollageRenderSettings.SourceMaxDimension / PaletteExportSettings), so mobile's
    // "Working size" setting actually controls collage speed/output size, same as it already
    // does for the single-photo path — a fixed constant here ignored that setting entirely.
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

    // Mirrors the ExportPreset → pixel-dimensions mapping in PaletteImageRenderer.RenderCore,
    // reduced to just the aspect ratio the composed collage canvas should target. Original has
    // no native aspect for a multi-photo collage (unlike a single photo), so it falls back to
    // a plain square.
    private static double ExportAspect(ExportPreset preset) => preset switch
    {
        ExportPreset.Square            => 1080.0 / 1080.0,
        ExportPreset.InstagramPost     => 1080.0 / 1350.0,
        ExportPreset.Story             => 1080.0 / 1920.0,
        ExportPreset.TelegramLandscape => 1920.0 / 1080.0,
        _                              => 1.0
    };
}
