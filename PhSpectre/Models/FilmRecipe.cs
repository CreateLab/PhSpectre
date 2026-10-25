using System.Text.Json.Serialization;

namespace PhSpectre.Models;

public sealed record FilmRecipe(
    RecipeSource Source,
    string? RecipeName,
    string CameraMake,
    string CameraModel,
    string? FilmSimulation,
    string? WhiteBalance,
    [property: JsonConverter(typeof(WhiteBalanceShiftJsonConverter))] (int Red, int Blue)? WhiteBalanceShift,
    string? DynamicRange,
    decimal? HighlightTone,
    decimal? ShadowTone,
    int? Color,
    int? Sharpness,
    int? NoiseReduction,
    decimal? Clarity,
    string? GrainEffect,
    string? ColorChromeEffect,
    string? ColorChromeFxBlue,
    int? BwAdjustment,
    // Slot-only settings (never in MakerNote, so the JPEG extractor leaves them null). Optional and last
    // so existing positional construction and stored JSON keep working.
    string? GrainSize = null,
    string? SmoothSkinEffect = null,
    int? ColorTemperature = null
);
