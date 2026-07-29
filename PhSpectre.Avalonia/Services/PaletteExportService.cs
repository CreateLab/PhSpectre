using System.Threading;
using System.Threading.Tasks;
using PhSpectre;
using PhSpectre.Rendering;

namespace PhSpectre.Avalonia.Services;

// The one place that turns "a source photo + a settings snapshot" into a saved palette
// file — used by both the single Save PNG/JPEG flow and batch export, so they can never
// drift apart in behavior.
public static class PaletteExportService
{
    public static async Task ExportAsync(
        string sourcePath, string destPath, PaletteExportSettings settings, CancellationToken cancellationToken,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null)
    {
        PhSpectre.Models.ColorPalette palette;
        await using (var fs = System.IO.File.OpenRead(sourcePath))
            palette = await new PaletteExtractor().ExtractAsync(fs, settings.Colors, settings.SamplingMode, cancellationToken);

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
            customBackground: settings.CustomBackground), cancellationToken);
    }
}
