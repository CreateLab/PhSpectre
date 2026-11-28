using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhSpectre;
using PhSpectre.Models;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Avalonia.Services;

public static class PaletteExportService
{
    // Stand-in when ComputeColors is false: ColorPalette needs at least one swatch.
    private static readonly PhSpectre.Models.ColorPalette NoColorsPlaceholder =
        new([new PhSpectre.Models.ColorSwatch("#000000", (0, 0, 0), 1f)]);

    public static async Task ExportAsync(
        string sourcePath, string destPath, PaletteExportSettings settings, CancellationToken cancellationToken,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null, FilmRecipe? recipe = null,
        PhSpectre.ColorGrading.GradeStack? look = null, PhSpectre.Rendering.PhotoAdjust? adjust = null)
    {
        // Decoded once for both the palette and the render.
        using var original = await Task.Run(() =>
        {
            using var fs = System.IO.File.OpenRead(sourcePath);
            var img = Image.Load<Rgb24>(fs);
            img.Mutate(ctx => ctx.AutoOrient());
            // The Look is part of the picture the card is built from.
            var prepared = Prepare(img, adjust, look);
            if (!ReferenceEquals(prepared, img)) img.Dispose();
            return prepared;
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Read the recipe from EXIF if the caller has none.
        recipe ??= PhSpectre.Recipes.RecipeReader.Read(original);

        await ExportAsync(original, destPath, settings, cancellationToken, metadataOverride, recipe);
    }

    // Crop, light and Look for a card. Returns the photo itself (graded in place) or a new, cropped one. Slow: keep
    // it off the UI thread.
    public static Image<Rgb24> Prepare(Image<Rgb24> photo, PhSpectre.Rendering.PhotoAdjust? adjust,
        PhSpectre.ColorGrading.GradeStack? look)
    {
        var result = adjust is { IsNoOp: false } ? adjust.Render(photo) : photo;
        look?.Apply(result);
        return result;
    }

    // ExportAsync for an already decoded, oriented image.
    public static async Task ExportAsync(
        Image<Rgb24> original, string destPath, PaletteExportSettings settings, CancellationToken cancellationToken,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null, FilmRecipe? recipe = null)
    {
        PhSpectre.Models.ColorPalette palette;
        if (settings.ComputeColors)
        {
            // The extractor works synchronously.
            palette = await Task.Run(() => new PaletteExtractor().ExtractAsync(original, settings.Colors,
                settings.SamplingMode, cancellationToken), cancellationToken);
        }
        else
        {
            palette = NoColorsPlaceholder;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The QR may include recipe or photo info in any mode, so it uses the renderer's metadata.
        var qr = settings.BuildQrRenderOptions(metadataOverride ?? PaletteImageRenderer.ReadMetadata(original), recipe);

        await Task.Run(() => PaletteImageRenderer.Render(
            original, palette, destPath,
            showHex:          settings.ShowHex,
            metaVerbosity:    settings.MetaVerbosity,
            metaStyle:        settings.MetaStyle,
            theme:            settings.Theme,
            hexBelow:         settings.HexBelow,
            showSwatches:     settings.ShowSwatches,
            downscale:        settings.HalfSize ? 2 : 1,
            format:           settings.Format,
            exportPreset:     settings.ExportPreset,
            metadataOverride: metadataOverride,
            labelScale:       settings.LabelScale,
            swatchScale:      settings.SwatchScale,
            showPercent:      settings.ShowPercent,
            swatchShape:      settings.SwatchShape,
            sortOrder:        settings.SortOrder,
            customBackground: settings.CustomBackground,
            compositionGuide: settings.CompositionGuide,
            useBlurredBackground: settings.UseBlurredBackground,
            qr:               qr), cancellationToken);
    }

    // ExportAsync for collages. None of the steps are async, so Task.Run keeps them off the UI thread.
    public static Task ExportCollageAsync(
        IReadOnlyList<string> sourcePaths, string destPath, PaletteExportSettings settings,
        CancellationToken cancellationToken,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null, FilmRecipe? recipe = null)
    {
        // The first tray photo supplies the EXIF.
        var qrMetadata = metadataOverride ?? PaletteImageRenderer.ReadMetadata(sourcePaths[0]);
        var qr = settings.BuildQrRenderOptions(qrMetadata, recipe);

        var collageSettings = new CollageRenderSettings(
            Colors:            settings.Colors,
            SamplingMode:      settings.SamplingMode,
            ShowHex:           settings.ShowHex,
            HexBelow:          settings.HexBelow,
            MetaVerbosity:     settings.MetaVerbosity,
            MetaStyle:         settings.MetaStyle,
            Theme:             settings.Theme,
            ShowSwatches:      settings.ShowSwatches,
            Format:            settings.Format,
            ExportPreset:      settings.ExportPreset,
            LabelScale:        settings.LabelScale,
            SwatchScale:       settings.SwatchScale,
            ShowPercent:       settings.ShowPercent,
            SwatchShape:       settings.SwatchShape,
            SortOrder:         settings.SortOrder,
            CustomBackground:  settings.CustomBackground,
            CompositionGuide:  settings.CompositionGuide,
            GutterColor:       settings.GutterColor,
            GutterThickness:   settings.GutterThickness,
            SourceMaxDimension: settings.CollageSourceMaxDimension,
            ComputeColors:     settings.ComputeColors,
            Qr:                qr);

        return Task.Run(
            () => CollageService.RenderCollageAsync(sourcePaths, destPath, collageSettings, cancellationToken, metadataOverride),
            cancellationToken);
    }

    // Layout-only preview of ExportCollageAsync.
    public static Task<Image<Rgb24>> ComposeCollagePreviewAsync(
        IReadOnlyList<string> sourcePaths, PaletteExportSettings settings, CancellationToken cancellationToken) =>
        CollageService.ComposePreviewAsync(sourcePaths, settings.GutterColor, settings.GutterThickness, settings.ExportPreset,
            settings.CollageSourceMaxDimension, cancellationToken);
}
