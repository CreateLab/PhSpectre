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
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null, FilmRecipe? recipe = null)
    {
        // Decoded once and reused for both palette extraction and the final render (see the
        // Image<Rgb24> overload below). Previously extraction opened its own FileStream (a full
        // decode) and the render separately passed sourcePath straight to
        // PaletteImageRenderer.Render, which decodes the same file a second time — for a
        // full-resolution camera photo, that's a real decode paid twice on every single export,
        // in every mode (it doesn't depend on ComputeColors/k-means at all).
        using var original = await Task.Run(() =>
        {
            using var fs = System.IO.File.OpenRead(sourcePath);
            var img = Image.Load<Rgb24>(fs);
            img.Mutate(ctx => ctx.AutoOrient());
            return img;
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Auto-detect the film recipe from the decoded image's own EXIF when the caller
        // didn't already resolve one (e.g. batch export, one photo at a time) — same
        // "auto-read unless overridden" convention as PhotoMetadata, and cheap since the
        // profile is already in memory, no extra disk read.
        recipe ??= PhSpectre.Recipes.RecipeReader.Read(original);

        await ExportAsync(original, destPath, settings, cancellationToken, metadataOverride, recipe);
    }

    // In-memory twin of ExportAsync above — for a caller (the desktop live-preview VM) that
    // already holds a decoded, auto-oriented working copy of the photo for its own on-screen
    // preview and would otherwise force a second full decode just to hand this method a path.
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

        // A QR content preset can include Recipe/PhotoInfo regardless of ExportMode (a Card
        // export can still carry a QR pointing at recipe data even though it draws no plate
        // grid itself) — resolve the same PhotoMetadata the renderer itself would use so the
        // QR's "Camera + Lens" fields agree with what's actually printed on the card.
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
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null, FilmRecipe? recipe = null)
    {
        // Collage's EXIF source is always the first tray photo (CollageService.RenderCollageAsync
        // re-derives it the same way when metadataOverride is null) — good enough for the QR's
        // Camera/Lens fields too; a QR-specific re-read isn't worth a second EXIF decode here.
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

    // Cheap live-preview twin of ExportCollageAsync — no palette, no card, just the
    // composed layout. See CollageService.ComposePreviewAsync.
    public static Task<Image<Rgb24>> ComposeCollagePreviewAsync(
        IReadOnlyList<string> sourcePaths, PaletteExportSettings settings, CancellationToken cancellationToken) =>
        CollageService.ComposePreviewAsync(sourcePaths, settings.GutterColor, settings.GutterThickness, settings.ExportPreset,
            settings.CollageSourceMaxDimension, cancellationToken);
}
