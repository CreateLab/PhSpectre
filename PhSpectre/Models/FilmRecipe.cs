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
    int? HighlightTone,
    int? ShadowTone,
    int? Color,
    int? Sharpness,
    int? NoiseReduction,
    decimal? Clarity,
    string? GrainEffect,
    string? ColorChromeEffect,
    string? ColorChromeFxBlue,
    int? BwAdjustment
);
