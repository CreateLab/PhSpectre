using PhSpectre.Models;
using PhSpectre.Rendering;

namespace PhSpectre.Qr;

// Builds a phspectre-view URL for a preset. Custom URLs bypass this.
public static class QrLinkBuilder
{
    private const string ViewerBaseUrl = "https://createlab.github.io/phspectre-view/";

    public static string BuildGeneratedUrl(
        QrContentPreset preset,
        PaletteImageRenderer.PhotoMetadata metadata,
        FilmRecipe? recipe,
        string? note,
        IQrLinkPayloadEncoder? encoder = null)
    {
        var fields = BuildFields(preset, metadata, recipe, note);
        var payload = (encoder ?? new BitPackedQrLinkPayloadEncoder()).Encode(fields);
        return $"{ViewerBaseUrl}#b.{payload}";
    }

    internal static QrLinkFields BuildFields(
        QrContentPreset preset, PaletteImageRenderer.PhotoMetadata metadata, FilmRecipe? recipe, string? note)
    {
        bool includeCamera = preset != QrContentPreset.RecipeOnly;
        var (includeLens, includePhotoInfo, includeRecipe) = preset switch
        {
            QrContentPreset.CameraAndRecipe               => (false, false, true),
            QrContentPreset.CameraLensPhotoInfoAndRecipe  => (true,  true,  true),
            QrContentPreset.CameraPhotoInfoAndRecipe      => (false, true,  true),
            QrContentPreset.CameraLensAndPhotoInfo        => (true,  true,  false),
            QrContentPreset.RecipeOnly                    => (false, false, true),
            _                                              => (false, false, false),
        };

        return new QrLinkFields(
            Camera:   includeCamera ? metadata.Camera : null,
            Lens:     includeLens ? metadata.Lens : null,
            Focal:    includePhotoInfo ? metadata.Focal : null,
            Aperture: includePhotoInfo ? metadata.Aperture : null,
            Shutter:  includePhotoInfo ? metadata.Shutter : null,
            Iso:      includePhotoInfo ? metadata.Iso : null,
            Recipe:   includeRecipe ? recipe : null,
            Note:     string.IsNullOrWhiteSpace(note) ? null : note);
    }
}
