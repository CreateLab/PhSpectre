using PhSpectre.Models;

namespace PhSpectre.Qr;

// The subset of a shot's data that can go into a generated-link QR payload, after a
// QrContentPreset has already zeroed out whatever fields it doesn't include. Camera/Lens/
// Focal/Aperture/Shutter/Iso mirror PaletteImageRenderer.PhotoMetadata's pre-formatted
// display strings (kept as strings here too, rather than re-parsing back to raw numeric
// EXIF types, since the viewer page only ever displays them).
public sealed record QrLinkFields(
    string? Camera,
    string? Lens,
    string? Focal,
    string? Aperture,
    string? Shutter,
    string? Iso,
    FilmRecipe? Recipe,
    string? Note);
