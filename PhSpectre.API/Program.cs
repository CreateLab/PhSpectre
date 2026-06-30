using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using PhSpectre;
using PhSpectre.Models;
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

app.MapPost("/api/palette", async (IFormFile? file, [FromForm] int? colors, [FromForm] string? theme, [FromForm] string? mode) =>
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

    var tmpIn  = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.jpg");
    var tmpOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");
    try
    {
        await using (var fs = File.Create(tmpIn))
            await file.CopyToAsync(fs);

        ColorPalette palette;
        await using (var fs = File.OpenRead(tmpIn))
            palette = await new PaletteExtractor().ExtractAsync(fs, colors, parsedMode);

        PaletteImageRenderer.Render(tmpIn, palette, tmpOut,
            showHex: true,
            metaVerbosity: MetaVerbosity.Default,
            metaStyle: MetaStyle.FilmStrip,
            theme: parsedTheme);

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
.Produces(400)
.Produces(429)
.Produces(500)
.WithOpenApi(op =>
{
    op.Summary = "Extract color palette from JPEG";
    op.Description = "Returns the original photo composited with its dominant color swatches as a PNG.";
    if (op.RequestBody?.Content.TryGetValue("multipart/form-data", out var content) == true)
    {
        content.Schema.Properties["file"] = new OpenApiSchema { Type = "string", Format = "binary" };
        content.Schema.Required.Add("file");
    }
    op.Responses["200"].Description = "PNG palette image";
    op.Responses["400"] = new OpenApiResponse { Description = "Invalid input (missing file, wrong format, bad theme/colors)" };
    op.Responses["429"] = new OpenApiResponse { Description = "Rate limit exceeded — 10 requests/minute per IP" };
    op.Responses["500"] = new OpenApiResponse { Description = "Internal rendering error" };
    return op;
});

app.Run();
