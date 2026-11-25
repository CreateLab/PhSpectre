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

// Turns a source photo and a settings snapshot into a saved palette file; shared by single save and batch export.
public static class PaletteExportService
{
    // Stand-in when ComputeColors is false: k-means never runs and the swatches aren't drawn, but ColorPalette needs at least one swatch.
    private static readonly PhSpectre.Models.ColorPalette NoColorsPlaceholder =
        new([new PhSpectre.Models.ColorSwatch("#000000", (0, 0, 0), 1f)]);

    public static async Task ExportAsync(
        string sourcePath, string destPath, PaletteExportSettings settings, CancellationToken cancellationToken,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null, FilmRecipe? recipe = null,
        PhSpectre.ColorGrading.GradeStack? look = null, PhSpectre.Rendering.PhotoAdjust? adjust = null)
    {
        // Decoded once and reused for palette extraction and the final render, instead of decoding the full-resolution file twice.
        using var original = await Task.Run(() =>
        {
            using var fs = System.IO.File.OpenRead(sourcePath);
            var img = Image.Load<Rgb24>(fs);
            img.Mutate(ctx => ctx.AutoOrient());
            if (adjust is { IsNoOp: false }) { var adjusted = adjust.Render(img); img.Dispose(); return adjusted; }
            return img;
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        look?.Apply(original); // the Look is part of the picture the card is built from

        // Auto-detect the film recipe from the image's EXIF when the caller didn't resolve one (cheap: the profile is in memory).
        recipe ??= PhSpectre.Recipes.RecipeReader.Read(original);

        await ExportAsync(original, destPath, settings, cancellationToken, metadataOverride, recipe);
    }

    // In-memory twin of ExportAsync, for callers that already hold a decoded, oriented working copy.
    public static async Task ExportAsync(
        Image<Rgb24> original, string destPath, PaletteExportSettings settings, CancellationToken cancellationToken,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null, FilmRecipe? recipe = null)
    {
        PhSpectre.Models.ColorPalette palette;
        if (settings.ComputeColors)
        {
            palette = await new PaletteExtractor().ExtractAsync(original, settings.Colors, settings.SamplingMode, cancellationToken);
        }
        else
        {
            palette = NoColorsPlaceholder;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // A QR preset can include Recipe/PhotoInfo in any mode; resolve the same PhotoMetadata the renderer uses so the QR agrees with the card.
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

    // Collage twin of ExportAsync: sources are composed, the palette is pooled by displayed area, then rendered as for a single photo.
    // None of the steps are real async I/O, so Task.Run keeps them off the calling (UI) thread.
    public static Task ExportCollageAsync(
        IReadOnlyList<string> sourcePaths, string destPath, PaletteExportSettings settings,
        CancellationToken cancellationToken,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null, FilmRecipe? recipe = null)
    {
        // The EXIF source is the first tray photo (as in CollageService.RenderCollageAsync); good enough for the QR's Camera/Lens.
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

    // Cheap live-preview twin of ExportCollageAsync: layout only (see CollageService.ComposePreviewAsync).
    public static Task<Image<Rgb24>> ComposeCollagePreviewAsync(
        IReadOnlyList<string> sourcePaths, PaletteExportSettings settings, CancellationToken cancellationToken) =>
        CollageService.ComposePreviewAsync(sourcePaths, settings.GutterColor, settings.GutterThickness, settings.ExportPreset,
            settings.CollageSourceMaxDimension, cancellationToken);
}
