using PhSpectre.Models;
using PhSpectre.Rendering;

namespace PhSpectre.Qr;

// Maps a QrContentPreset (the 5 rows of the spec's field table) onto the shot's already-read
// PhotoMetadata/FilmRecipe, then hands the result to an IQrLinkPayloadEncoder to build the
// final phspectre-view URL. Custom-URL mode never goes through this class — callers use the
// user's raw string directly.
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
