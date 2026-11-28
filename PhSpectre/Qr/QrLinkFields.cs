using PhSpectre.Models;

namespace PhSpectre.Qr;

// The fields a preset keeps, as display strings.
public sealed record QrLinkFields(
    string? Camera,
    string? Lens,
    string? Focal,
    string? Aperture,
    string? Shutter,
    string? Iso,
    FilmRecipe? Recipe,
    string? Note);
