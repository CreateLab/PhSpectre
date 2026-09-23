using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using PhSpectre;
using PhSpectre.Models;
using PhSpectre.Recipes;
using PhSpectre.Rendering;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "PhSpectre API", Version = "v1" });
});

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("fixed-per-ip", context =>
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
    options.RejectionStatusCode = 429;
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseRateLimiter();

app.MapPost("/api/palette", async (IFormFile? file, [FromForm] int? colors, [FromForm] string? theme, [FromForm] string? mode,
    [FromForm] string? sort, [FromForm] string? shape, [FromForm] bool? showPercent, [FromForm] string? guide,
    [FromForm] bool? json) =>
{
    if (file == null)
        return Results.Json(new { error = "file is required" }, statusCode: 400);

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext != ".jpg" && ext != ".jpeg")
        return Results.Json(new { error = "Only JPEG files (.jpg/.jpeg) are supported" }, statusCode: 400);

    Theme parsedTheme = Theme.Dark;
    if (!string.IsNullOrEmpty(theme))
    {
        if      (theme.Equals("light", StringComparison.OrdinalIgnoreCase)) parsedTheme = Theme.Light;
        else if (theme.Equals("dark",  StringComparison.OrdinalIgnoreCase)) parsedTheme = Theme.Dark;
        else return Results.Json(new { error = "theme must be 'dark' or 'light'" }, statusCode: 400);
    }

    SamplingMode parsedMode = SamplingMode.Vivid;
    if (!string.IsNullOrEmpty(mode))
    {
        if      (mode.Equals("vivid",    StringComparison.OrdinalIgnoreCase)) parsedMode = SamplingMode.Vivid;
        else if (mode.Equals("standard", StringComparison.OrdinalIgnoreCase)) parsedMode = SamplingMode.Standard;
        else if (mode.Equals("contrast", StringComparison.OrdinalIgnoreCase)) parsedMode = SamplingMode.Contrast;
        else return Results.Json(new { error = "mode must be 'vivid', 'standard' or 'contrast'" }, statusCode: 400);
    }

    if (colors is < 1 or > 32)
        return Results.Json(new { error = "colors must be between 1 and 32" }, statusCode: 400);

    SortOrder parsedSort = SortOrder.None;
    if (!string.IsNullOrEmpty(sort))
    {
        if      (sort.Equals("none",      StringComparison.OrdinalIgnoreCase)) parsedSort = SortOrder.None;
        else if (sort.Equals("hue",       StringComparison.OrdinalIgnoreCase)) parsedSort = SortOrder.Hue;
        else if (sort.Equals("luminance", StringComparison.OrdinalIgnoreCase)) parsedSort = SortOrder.Luminance;
        else if (sort.Equals("percent",   StringComparison.OrdinalIgnoreCase)) parsedSort = SortOrder.Percent;
        else return Results.Json(new { error = "sort must be 'none', 'hue', 'luminance' or 'percent'" }, statusCode: 400);
    }

    SwatchShape parsedShape = SwatchShape.Rectangle;
    if (!string.IsNullOrEmpty(shape))
    {
        if      (shape.Equals("rect",    StringComparison.OrdinalIgnoreCase)) parsedShape = SwatchShape.Rectangle;
        else if (shape.Equals("rounded", StringComparison.OrdinalIgnoreCase)) parsedShape = SwatchShape.Rounded;
        else if (shape.Equals("circle",  StringComparison.OrdinalIgnoreCase)) parsedShape = SwatchShape.Circle;
        else return Results.Json(new { error = "shape must be 'rect', 'rounded' or 'circle'" }, statusCode: 400);
    }

    CompositionGuide parsedGuide = CompositionGuide.None;
    if (!string.IsNullOrEmpty(guide))
    {
        if      (guide.Equals("none",     StringComparison.OrdinalIgnoreCase)) parsedGuide = CompositionGuide.None;
        else if (guide.Equals("thirds",   StringComparison.OrdinalIgnoreCase)) parsedGuide = CompositionGuide.RuleOfThirds;
        else if (guide.Equals("golden",   StringComparison.OrdinalIgnoreCase)) parsedGuide = CompositionGuide.GoldenRatio;
        else if (guide.Equals("diagonal", StringComparison.OrdinalIgnoreCase)) parsedGuide = CompositionGuide.Diagonal;
        else if (guide.Equals("cross",    StringComparison.OrdinalIgnoreCase)) parsedGuide = CompositionGuide.CenterCross;
        else return Results.Json(new { error = "guide must be 'none', 'thirds', 'golden', 'diagonal' or 'cross'" }, statusCode: 400);
    }

    var tmpIn  = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.jpg");
    var tmpOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");
    try
    {
        await using (var fs = File.Create(tmpIn))
            await file.CopyToAsync(fs);

        ColorPalette palette;
        await using (var fs = File.OpenRead(tmpIn))
            palette = await new PaletteExtractor().ExtractAsync(fs, colors, parsedMode);

        // json=true skips the PNG render entirely — the caller wants structured data
        // (swatches + recipe), not the composited image, so there's nothing to render.
        if (json == true)
        {
            var recipe = RecipeReader.Read(tmpIn);
            return Results.Json(new PaletteJsonResponse(palette.Swatches, recipe));
        }

        PaletteImageRenderer.Render(tmpIn, palette, tmpOut,
            showHex: true,
            metaVerbosity: MetaVerbosity.Default,
            metaStyle: MetaStyle.FilmStrip,
            theme: parsedTheme,
            sortOrder: parsedSort,
            swatchShape: parsedShape,
            showPercent: showPercent ?? false,
            compositionGuide: parsedGuide);

        var bytes = await File.ReadAllBytesAsync(tmpOut);
        return Results.File(bytes, "application/octet-stream", "palette.png");
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
    finally
    {
        if (File.Exists(tmpIn))  File.Delete(tmpIn);
        if (File.Exists(tmpOut)) File.Delete(tmpOut);
    }
})
.DisableAntiforgery()
.RequireRateLimiting("fixed-per-ip")
.WithName("PostPalette")
.Produces<byte[]>(200, "application/octet-stream")
.Produces<PaletteJsonResponse>(200, "application/json")
.Produces(400)
.Produces(429)
.Produces(500)
.WithOpenApi(op =>
{
    op.Summary = "Extract color palette from JPEG";
    op.Description = "Returns the original photo composited with its dominant color swatches as a PNG, " +
        "or (with json=true) the swatches and any detected Fujifilm film recipe as JSON instead of an image.";
    // content.Schema can itself be null here once the handler has enough [FromForm]
    // parameters (observed once `json` became the 9th) — a minimal-API OpenAPI-generation
    // quirk unrelated to the request handling itself, so guard it rather than crash route
    // building for the whole app.
    if (op.RequestBody?.Content.TryGetValue("multipart/form-data", out var content) == true
        && content.Schema != null)
    {
        content.Schema.Properties["file"] = new OpenApiSchema { Type = "string", Format = "binary" };
        content.Schema.Required.Add("file");
    }
    op.Responses["200"].Description = "PNG palette image, or (json=true) a JSON body with swatches + recipe";
    op.Responses["400"] = new OpenApiResponse { Description = "Invalid input (missing file, wrong format, bad theme/colors/sort/shape/guide)" };
    op.Responses["429"] = new OpenApiResponse { Description = "Rate limit exceeded — 10 requests/minute per IP" };
    op.Responses["500"] = new OpenApiResponse { Description = "Internal rendering error" };
    return op;
});

app.Run();

// json=true response shape for POST /api/palette — Recipe is null when the photo isn't a
// recognized Fuji camera or no MakerNote could be parsed (never an error in that case).
internal sealed record PaletteJsonResponse(IReadOnlyList<ColorSwatch> Swatches, FilmRecipe? Recipe);
