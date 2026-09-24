using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhSpectre;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Avalonia.Services;

// The one place that turns "a source photo + a settings snapshot" into a saved palette
// file — used by both the single Save PNG/JPEG flow and batch export, so they can never
// drift apart in behavior.
public static class PaletteExportService
{
    // Stand-in used whenever ComputeColors is false (InfoOnly/Recipe/CollageInfoOnly) —
    // k-means never runs, so there's nothing real to put here; showSwatches:false on the
    // Render call means it's never drawn. ColorPalette's constructor requires at least one
    // swatch, so this is the minimal legal instance rather than an empty one.
    private static readonly PhSpectre.Models.ColorPalette NoColorsPlaceholder =
        new([new PhSpectre.Models.ColorSwatch("#000000", (0, 0, 0), 1f)]);

    public static async Task ExportAsync(
        string sourcePath, string destPath, PaletteExportSettings settings, CancellationToken cancellationToken,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null)
    {
        PhSpectre.Models.ColorPalette palette;
        if (settings.ComputeColors)
        {
            await using var fs = System.IO.File.OpenRead(sourcePath);
            palette = await new PaletteExtractor().ExtractAsync(fs, settings.Colors, settings.SamplingMode, cancellationToken);
        }
        else
        {
            palette = NoColorsPlaceholder;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(() => PaletteImageRenderer.Render(
            sourcePath, palette, destPath,
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
            useBlurredBackground: settings.UseBlurredBackground), cancellationToken);
    }

    // Collage twin of ExportAsync — several source photos become one composed collage
    // (see CollageComposer), palette pooled across them by displayed area, then rendered
    // through the same PaletteImageRenderer pipeline as a single photo.
    //
    // Unlike ExportAsync, none of CollageService.RenderCollageAsync's steps (decoding N
    // full-res photos, compositing, sampling, k-means, drawing/encoding) are genuinely
    // async I/O — left unwrapped, the whole pipeline would run synchronously on whichever
    // thread called this (the UI thread, when invoked from the Avalonia VMs), freezing it
    // for the full render. Task.Run pushes all of it onto the thread pool instead.
    public static Task ExportCollageAsync(
        IReadOnlyList<string> sourcePaths, string destPath, PaletteExportSettings settings,
        CancellationToken cancellationToken,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null)
    {
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
            ComputeColors:     settings.ComputeColors);

        return Task.Run(
            () => CollageService.RenderCollageAsync(sourcePaths, destPath, collageSettings, cancellationToken, metadataOverride),
            cancellationToken);
    }

    // Cheap live-preview twin of ExportCollageAsync — no palette, no card, just the
    // composed layout. See CollageService.ComposePreviewAsync.
    public static Task<Image<Rgb24>> ComposeCollagePreviewAsync(
        IReadOnlyList<string> sourcePaths, PaletteExportSettings settings, CancellationToken cancellationToken) =>
        CollageService.ComposePreviewAsync(sourcePaths, settings.GutterColor, settings.GutterThickness, settings.ExportPreset,
            settings.CollageSourceMaxDimension, cancellationToken);
}
