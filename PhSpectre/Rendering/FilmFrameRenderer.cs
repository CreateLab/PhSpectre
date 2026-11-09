using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Rendering;

// Draws a FilmStyle around a photo. The edge, the sprocket holes, the per-zone data and the "film feel"
// (grain, uneven light, dust) are all drawn BEFORE the photo, so the photo's pixels are composited as-is;
// only its four corners are trimmed when the style asks for rounded corners.
//
// Everything random is seeded by the frame number and positioned relative to the canvas, so the live
// preview and the full-size export of the same frame look the same.
public static class FilmFrameRenderer
{
    // Sprocket holes take this share of a margin (the part next to the photo); text uses the rest.
    private const float HoleLane = 0.66f;

    // A real strip runs along the long side of the picture. A style made for a vertical strip is turned a quarter
    // for a landscape photo (and a horizontal one for a portrait photo): the photo is turned, framed, and turned back.
    // Half-frame is the exception: its strip runs along the short side (18 mm), so the picture is turned the other way.
    private static bool NeedsTurn(FilmStyle s, int w, int h) =>
        s.Gauge != FilmGauge.Free && w != h && s.VerticalStrip == (s.Gauge == FilmGauge.Mm135Half ? h > w : w > h);

    public static (int Width, int Height, int PhotoX, int PhotoY) Layout(FilmStyle style, int photoW, int photoH)
    {
        var s = style.Normalized();
        if (NeedsTurn(s, photoW, photoH))
        {
            // Framed as (photoH x photoW), then the whole canvas turned 90 degrees clockwise.
            var (cw, ch, x, y) = LayoutCore(s, photoH, photoW);
            return (ch, cw, ch - y - photoW, x);
        }
        return LayoutCore(s, photoW, photoH);
    }

    private static (int Width, int Height, int PhotoX, int PhotoY) LayoutCore(FilmStyle s, int photoW, int photoH)
    {
        int shortSide = Math.Min(photoW, photoH);
        int side = (int)Math.Round(shortSide * s.SidePercent / 100f);
        int top = (int)Math.Round(shortSide * s.TopPercent / 100f);
        int bottom = (int)Math.Round(shortSide * s.BottomPercent / 100f);
        return (photoW + 2 * side, photoH + top + bottom, side, top);
    }

    // The text printed for a token, or "" when there is nothing real to print (the zone then just stays empty).
    // FilmSim is the applied Look's name, or else the film simulation read from the photo: never an invented name.
    // frameLabel: the text printed for the frame number; when null the plain frameNumber is used (0 prints nothing).
    public static string TokenText(FilmToken token, PaletteImageRenderer.PhotoMetadata? m, int frameNumber, string custom,
        string? lookName = null, string? frameLabel = null) => token switch
    {
        FilmToken.FilmSim => (!string.IsNullOrWhiteSpace(lookName) ? lookName.Trim() : m?.FilmSimulation ?? "").ToUpperInvariant(),
        FilmToken.Shutter => m?.Shutter ?? "",
        FilmToken.Aperture => m?.Aperture ?? "",
        FilmToken.Iso => m?.Iso ?? "",
        FilmToken.Focal => m?.Focal ?? "",
        FilmToken.ExposureBias => m?.ExposureBias ?? "",
        FilmToken.ColorProfile => m?.ColorProfile ?? "",
        FilmToken.Date => m?.Date ?? "",
        FilmToken.Camera => m?.Camera ?? "",
        FilmToken.Lens => m?.Lens ?? "",
        FilmToken.FrameNumber => FrameText(frameNumber, frameLabel),
        FilmToken.FrameNumberA => FrameText(frameNumber, frameLabel) is { Length: > 0 } f ? f + "A" : "",
        FilmToken.Custom => (custom ?? "").ToUpperInvariant(),
        _ => ""
    };

    private static string FrameText(int frameNumber, string? frameLabel) =>
        frameLabel != null ? frameLabel.Trim() : frameNumber > 0 ? frameNumber.ToString() : "";

    private static List<string> ZoneTokens(FilmStyle style, FilmZone zone, PaletteImageRenderer.PhotoMetadata? m, int frameNumber,
        string? lookName, string? frameLabel)
    {
        if (!style.Zones.TryGetValue(zone, out var tokens) || tokens == null) return [];
        return tokens.Select(t => TokenText(t, m, frameNumber, style.CustomText, lookName, frameLabel)).Where(t => t.Length > 0).ToList();
    }

    public static string ZoneText(FilmStyle style, FilmZone zone, PaletteImageRenderer.PhotoMetadata? m, int frameNumber,
        string? lookName = null, string? frameLabel = null) =>
        string.Join("  ", ZoneTokens(style, zone, m, frameNumber, lookName, frameLabel));

    // lookName: the applied Look (LUT / preset), printed by the FilmSim token ahead of the photo's own film simulation.
    // frameNumber also seeds the dust/grain; frameLabel (when not null) is what the frame-number tokens print.
    public static Image<Rgb24> Render(Image<Rgb24> photo, FilmStyle style, PaletteImageRenderer.PhotoMetadata? meta, int frameNumber = 1,
        string? lookName = null, string? frameLabel = null)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var s = style.Normalized();
        if (!NeedsTurn(s, photo.Width, photo.Height)) return RenderCore(photo, s, meta, frameNumber, lookName, frameLabel);
        using var turned = photo.Clone(c => c.Rotate(RotateMode.Rotate270));
        var framed = RenderCore(turned, s, meta, frameNumber, lookName, frameLabel);
        framed.Mutate(c => c.Rotate(RotateMode.Rotate90));
        return framed;
    }

    private static Image<Rgb24> RenderCore(Image<Rgb24> photo, FilmStyle s, PaletteImageRenderer.PhotoMetadata? meta, int frameNumber,
        string? lookName, string? frameLabel)
    {
        var (w, h, px, py) = Layout(s, photo.Width, photo.Height);
        int side = px, top = py, bottom = h - photo.Height - py;
        int shortSide = Math.Min(photo.Width, photo.Height);
        float real = s.Realism / 100f;
        var rng = new Random(unchecked(frameNumber * 7919 + 17));

        Color Parse(string hex, Color fallback) => HexColorInput.TryParse(hex, out var c) ? c : fallback;
        var edge = Parse(s.EdgeColorHex, Color.Black);
        var hole = Parse(s.HoleColorHex, Color.Black);
        var text = Parse(s.TextColorHex, Color.White);

        var canvas = new Image<Rgb24>(w, h, edge.ToPixel<Rgb24>());
        var gapTokens = ZoneTokens(s, FilmZone.HoleGaps, meta, frameNumber, lookName, frameLabel);

        // Sprocket holes (and the lane left for text next to them).
        bool sideHoles = s.Perforation == FilmPerforation.Sides && side > 0;
        bool rowHoles = s.Perforation == FilmPerforation.Rows;
        bool hasGaps = gapTokens.Count > 0;
        bool real135 = s.IsMm135;
        // Share of a holed margin left for text on the outer side of the holes.
        float outerShare = real135 ? FilmStyle.Mm135HoleEdge / FilmStyle.Mm135Across : 1 - HoleLane;
        HoleRun? gapRun = null;
        if (sideHoles)
        {
            var left = DrawHoleRun(canvas, hole, vertical: true, margin: side, origin: 0, length: h, hasGaps, real, rng, mirror: false, real135);
            DrawHoleRun(canvas, hole, vertical: true, margin: side, origin: w - side, length: h, hasGaps, real, rng, mirror: true, real135);
            gapRun = left;
        }
        if (rowHoles && top > 0) DrawHoleRun(canvas, hole, vertical: false, margin: top, origin: 0, length: w, hasGaps, real, rng, mirror: false, real135);
        if (rowHoles && bottom > 0) gapRun = DrawHoleRun(canvas, hole, vertical: false, margin: bottom, origin: h - bottom, length: w, hasGaps, real, rng, mirror: true, real135);
        else if (rowHoles && gapRun == null) gapRun = null;

        float topLane = rowHoles ? top * outerShare : top;
        float bottomLane = rowHoles ? bottom * outerShare : bottom;
        float sideLane = sideHoles ? side * outerShare : side;
        float topCy = topLane / 2;                                 // text sits on the outer-edge side of its margin
        float bottomCy = h - bottomLane / 2;

        float zoneW = w * 0.32f, inset = w * 0.035f;
        Draw(FilmZone.TopCenter, w / 2f, topCy, topLane, w * 0.8f, 0);
        Draw(FilmZone.TopLeft, inset, topCy, topLane, zoneW, 0);
        Draw(FilmZone.TopRight, w - inset, topCy, topLane, zoneW, 0);
        Draw(FilmZone.BottomLeft, inset, bottomCy, bottomLane, zoneW, 0);
        Draw(FilmZone.BottomCenter, w / 2f, bottomCy, bottomLane, zoneW, 0);
        Draw(FilmZone.BottomRight, w - inset, bottomCy, bottomLane, zoneW, 0);
        // Sides read bottom-to-top on the left and top-to-bottom on the right, like printed edge marks.
        Draw(FilmZone.SideLeft, sideLane / 2, h / 2f, sideLane, h * 0.7f, 270);
        Draw(FilmZone.SideRight, w - sideLane / 2, h / 2f, sideLane, h * 0.7f, 90);

        // Data between the sprocket holes: one value per gap, spread along the strip.
        if (hasGaps && gapRun is { Count: > 1 } run)
        {
            int gaps = run.Count - 1, n = gapTokens.Count;
            for (int j = 0; j < n; j++)
            {
                int g = Math.Clamp((int)Math.Round((j + 1) * (double)run.Count / (n + 1)) - 1, 0, gaps - 1);
                float along = run.Start + (g + 0.5f) * run.Pitch;
                float cx = run.Vertical ? run.Center : along, cy = run.Vertical ? along : run.Center;
                DrawText(canvas, gapTokens[j], text, s.Glow, real, cx, cy, run.Lane * 0.46f, run.Pitch - run.HoleLength * (real135 ? 1.08f : 1.25f),
                    run.Vertical ? 270 : 0);
            }
        }

        var photoRect = new Rectangle(px, py, photo.Width, photo.Height);
        if (real > 0) Texture(canvas, photoRect, real, frameNumber);
        if (s.Dust > 0) Dust(canvas, shortSide, s.Dust / 100f, edge, rng);

        // The photo goes on last (untouched); rounded corners restore the edge underneath the trimmed pixels.
        int r = Math.Clamp((int)Math.Round(shortSide * s.CornerPercent / 100f), 0, Math.Min(photo.Width, photo.Height) / 4);
        var saved = r > 1 ? SaveCorners(canvas, photoRect, r) : null;
        canvas.Mutate(m => m.DrawImage(photo, new Point(px, py), 1f));
        if (saved != null) RestoreCorners(canvas, photoRect, r, saved);
        return canvas;

        void Draw(FilmZone zone, float cx, float cy, float lane, float maxLen, int rotation)
        {
            var t = ZoneText(s, zone, meta, frameNumber, lookName, frameLabel);
            if (t.Length == 0 || lane < 4f) return;
            // A name (Look / film simulation) is the one bold, large line on the strip beyond the sprockets;
            // everything else stays small, like the short codes printed on real stock.
            bool name = s.Zones.TryGetValue(zone, out var toks) && toks.Count > 0
                && toks[0] is FilmToken.FilmSim or FilmToken.FrameNumber or FilmToken.FrameNumberA;
            // Corner zones hug the edge (left-aligned on the left, right-aligned on the right); the rest is centred.
            int align = zone is FilmZone.TopLeft or FilmZone.BottomLeft ? -1 : zone is FilmZone.TopRight or FilmZone.BottomRight ? 1 : 0;
            DrawText(canvas, t, text, s.Glow, real, cx, cy, lane * (name ? 0.78f : 0.55f), maxLen, rotation, bold: name, align: align);
        }
    }

    private static void DrawText(Image<Rgb24> canvas, string text, Color color, bool glow, float real,
        float cx, float cy, float fontSize, float maxLen, int rotation, bool bold = false, int align = 0)
    {
        if (maxLen < 8f) return;
        var weight = bold ? FontStyle.Bold : FontStyle.Regular;
        var font = PaletteImageRenderer.ResolveMetaFont(Math.Max(6f, fontSize), weight);
        float len = TextMeasurer.MeasureAdvance(text, new TextOptions(font)).Width;
        if (len > maxLen && len > 0)
        {
            font = PaletteImageRenderer.ResolveMetaFont(Math.Max(5f, font.Size * maxLen / len), weight);
            len = TextMeasurer.MeasureAdvance(text, new TextOptions(font)).Width;
        }

        int pad = (int)Math.Ceiling(font.Size * 0.6f);
        int lw = (int)Math.Ceiling(len) + pad * 2, lh = (int)Math.Ceiling(font.Size * 1.3f) + pad * 2;
        using var layer = new Image<Rgba32>(lw, lh);
        var opts = new RichTextOptions(font)
        {
            Origin = new PointF(lw / 2f, lh / 2f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        layer.Mutate(c => c.DrawText(opts, text, Brushes.Solid(color)));
        // Printed/exposed ink is never razor sharp.
        if (real > 0) layer.Mutate(c => c.GaussianBlur(Math.Max(0.45f, font.Size * 0.02f * (0.5f + real))).Opacity(0.9f));
        if (rotation == 90) layer.Mutate(c => c.Rotate(RotateMode.Rotate90));
        else if (rotation == 270) layer.Mutate(c => c.Rotate(RotateMode.Rotate270));

        // align -1/+1 anchors the text's left/right end at cx instead of centring it (the layer has `pad` of air around the text).
        float left = align < 0 ? cx - pad : align > 0 ? cx - layer.Width + pad : cx - layer.Width / 2f;
        var at = new Point((int)Math.Round(left), (int)Math.Round(cy - layer.Height / 2f));
        if (glow)
        {
            using var bloom = layer.Clone();
            bloom.Mutate(c => c.GaussianBlur(Math.Max(1f, font.Size * 0.25f)));
            canvas.Mutate(c => c.DrawImage(bloom, at, 1f).DrawImage(bloom, at, 1f));
        }
        canvas.Mutate(c => c.DrawImage(layer, at, 1f));
    }

    // Where a run of sprocket holes sits: positions along the strip, so data can be printed in the gaps.
    private sealed record HoleRun(bool Vertical, float Start, float Pitch, int Count, float Center, float Lane, float HoleLength);

    // A row/column of rounded-rectangle holes along a margin, next to the photo. `mirror` puts the lane on the
    // far side (right column / bottom row). With data in the gaps the holes are spread further apart.
    private static HoleRun DrawHoleRun(Image<Rgb24> canvas, Color hole, bool vertical, int margin, int origin, int length,
        bool hasGaps, float real, Random rng, bool mirror, bool real135)
    {
        float lane = margin * HoleLane;
        var rim = Color.White.WithAlpha(0.10f + 0.12f * real);
        // Hole across the margin (thickness) and along it (len).
        float thick = lane * 0.86f, along = thick * 1.2f;
        float pitch = along * (hasGaps ? 3.4f : 1.5f);
        // The lane hugs the photo: inner edge of a left/top margin, near edge of a right/bottom one.
        float center = mirror ? origin + lane / 2f : origin + margin - lane / 2f;
        if (real135)
        {
            // True KS perforation: the margin is 5.5 mm, so one mm is margin / 5.5 pixels. Holes sit 2.01 mm in from
            // the film edge (the outer edge is the origin of a left/top margin and the far end of a right/bottom one).
            float mm = margin / FilmStyle.Mm135Across;
            thick = FilmStyle.Mm135HoleAcross * mm; along = FilmStyle.Mm135HoleAlong * mm; pitch = FilmStyle.Mm135Pitch * mm;
            lane = thick * 1.3f;
            float fromEdge = (FilmStyle.Mm135HoleEdge + FilmStyle.Mm135HoleAcross / 2) * mm;
            center = mirror ? origin + margin - fromEdge : origin + fromEdge;
        }
        // The row is centred, then carried on to both ends of the strip: the outermost holes are cut by the edge of
        // the canvas, as on a real strip, instead of leaving bare corners.
        int whole = Math.Max(0, (int)((length - along) / pitch));
        float centred = whole > 0 ? (length - (whole - 1) * pitch) / 2f : length / 2f;
        int first = (int)Math.Floor(-(centred + along / 2) / pitch) + 1;
        int last = (int)Math.Ceiling((length + along / 2 - centred) / pitch) - 1;
        int n = Math.Max(0, last - first + 1);
        float start = centred + first * pitch;

        canvas.Mutate(c =>
        {
            for (int i = 0; i < n; i++)
            {
                // Real stock isn't perfectly regular: a little wobble in size and position.
                float jw = 1 + (float)(rng.NextDouble() - 0.5) * 0.10f * real, jh = 1 + (float)(rng.NextDouble() - 0.5) * 0.10f * real;
                float jp = (float)(rng.NextDouble() - 0.5) * 0.06f * real * pitch, jc = (float)(rng.NextDouble() - 0.5) * 0.08f * thick * real;
                float a = along * jw, t = thick * jh, pos = start + i * pitch + jp, cen = center + jc;
                var path = vertical
                    ? RoundedRect(cen - t / 2, pos - a / 2, t, a, t * 0.26f)
                    : RoundedRect(pos - a / 2, cen - t / 2, a, t, t * 0.26f);
                c.Fill(hole, path);
                // Light leaking round a punched hole leaves a faint rim.
                if (real > 0) c.Draw(rim, Math.Max(0.8f, t * 0.04f), path);
            }
        });

        if (real > 0 && n > 0)
        {
            // Soften the hole edges (punched stock is never a clean vector shape).
            float sigma = Math.Max(0.5f, thick * 0.05f * real);
            var strip = vertical
                ? new Rectangle((int)(center - lane / 2), 0, (int)lane + 1, length)
                : new Rectangle(0, (int)(center - lane / 2), length, (int)lane + 1);
            strip.Intersect(new Rectangle(0, 0, canvas.Width, canvas.Height));
            if (strip.Width > 0 && strip.Height > 0) canvas.Mutate(c => c.GaussianBlur(sigma, strip));
        }
        return new HoleRun(vertical, start, pitch, n, center, lane, along);
    }

    // Film feel over the edge only (the photo area is skipped): grain, slow uneven light and a vignette.
    // One pass over the pixels with a coarse random grid sampled bilinearly for the uneven light.
    private static void Texture(Image<Rgb24> img, Rectangle skip, float real, int seed)
    {
        int w = img.Width, h = img.Height;
        const int gx = 9, gy = 7;
        var rnd = new Random(unchecked(seed * 104729 + 3));
        var grid = new float[(gx + 1) * (gy + 1)];
        for (int i = 0; i < grid.Length; i++) grid[i] = (float)(rnd.NextDouble() * 2 - 1);
        float shadeAmp = 0.11f * real, vig = 0.30f * real, grainAmp = 24f * real;

        {
            Parallel.For(0, h, y =>
            {
                var row = img.DangerousGetPixelRowMemory(y).Span;
                uint st = unchecked((uint)(seed * 2654435761u) ^ ((uint)(y + 1) * 40503u)) | 1u;
                float fy = y / (float)Math.Max(1, h - 1) * gy;
                int y0 = Math.Min((int)fy, gy - 1);
                float ty = fy - y0, ny = (y / (float)h - 0.5f) * 2;
                bool rowInPhoto = y >= skip.Top && y < skip.Bottom;
                for (int x = 0; x < w; x++)
                {
                    if (rowInPhoto && x == skip.Left) { x = skip.Right - 1; continue; }
                    float fx = x / (float)Math.Max(1, w - 1) * gx;
                    int x0 = Math.Min((int)fx, gx - 1);
                    float tx = fx - x0;
                    float g00 = grid[y0 * (gx + 1) + x0], g10 = grid[y0 * (gx + 1) + x0 + 1];
                    float g01 = grid[(y0 + 1) * (gx + 1) + x0], g11 = grid[(y0 + 1) * (gx + 1) + x0 + 1];
                    float shade = (g00 * (1 - tx) + g10 * tx) * (1 - ty) + (g01 * (1 - tx) + g11 * tx) * ty;
                    float nx = (x / (float)w - 0.5f) * 2;
                    float mult = 1 + shade * shadeAmp - vig * (nx * nx + ny * ny) * 0.5f;

                    st ^= st << 13; st ^= st >> 17; st ^= st << 5;
                    float u1 = (st & 0xFFFF) / 65535f;
                    float u2 = (st >> 16) / 65535f;
                    float grain = (u1 + u2 - 1f) * grainAmp;

                    ref var p = ref row[x];
                    p.R = ToByte(p.R * mult + grain);
                    p.G = ToByte(p.G * mult + grain);
                    p.B = ToByte(p.B * mult + grain);
                }
            });
        }
    }

    private static byte ToByte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;

    // Dust specks and hair-line scratches, in the colour that shows against the edge.
    private static void Dust(Image<Rgb24> canvas, int shortSide, float amount, Color edge, Random rng)
    {
        int w = canvas.Width, h = canvas.Height;
        var e = edge.ToPixel<Rgb24>();
        bool lightEdge = (e.R * 0.3 + e.G * 0.59 + e.B * 0.11) > 128;
        canvas.Mutate(c =>
        {
            int specks = (int)(30 + 150 * amount);
            for (int i = 0; i < specks; i++)
            {
                float x = (float)rng.NextDouble() * w, y = (float)rng.NextDouble() * h;
                float rad = shortSide * (0.0005f + (float)Math.Pow(rng.NextDouble(), 3) * 0.004f);
                bool dark = lightEdge ? rng.NextDouble() < 0.8 : rng.NextDouble() < 0.15;
                var col = (dark ? Color.Black : Color.White).WithAlpha(0.25f + (float)rng.NextDouble() * 0.5f * amount + 0.15f);
                c.Fill(col, new EllipsePolygon(x, y, rad, rad * (0.6f + (float)rng.NextDouble() * 0.8f)));
            }
            int scratches = (int)(1 + 7 * amount);
            for (int i = 0; i < scratches; i++)
            {
                float x = (float)rng.NextDouble() * w, y = (float)rng.NextDouble() * h * 0.6f;
                float len = h * (0.08f + (float)rng.NextDouble() * 0.45f);
                var pts = new List<PointF>();
                for (int k = 0; k <= 6; k++)
                    pts.Add(new PointF(x + (float)(rng.NextDouble() - 0.5) * shortSide * 0.003f, y + len * k / 6f));
                var col = (lightEdge ? Color.Black : Color.White).WithAlpha(0.14f + 0.2f * amount);
                c.DrawLine(col, Math.Max(0.6f, shortSide * 0.0008f), pts.ToArray());
            }
        });
    }

    private static Rgb24[][] SaveCorners(Image<Rgb24> img, Rectangle p, int r)
    {
        var o = new Rgb24[4][];
        var origins = CornerOrigins(p, r);
        for (int k = 0; k < 4; k++)
        {
            o[k] = new Rgb24[r * r];
            for (int dy = 0; dy < r; dy++)
                for (int dx = 0; dx < r; dx++)
                    o[k][dy * r + dx] = img[origins[k].X + dx, origins[k].Y + dy];
        }
        return o;
    }

    private static Point[] CornerOrigins(Rectangle p, int r) =>
        [new(p.Left, p.Top), new(p.Right - r, p.Top), new(p.Left, p.Bottom - r), new(p.Right - r, p.Bottom - r)];

    // Blends the saved edge back in outside each corner arc (anti-aliased), leaving the rest of the photo as it was.
    private static void RestoreCorners(Image<Rgb24> img, Rectangle p, int r, Rgb24[][] saved)
    {
        var origins = CornerOrigins(p, r);
        // Arc centre of each corner, in the same order as the origins.
        var centers = new[]
        {
            new PointF(p.Left + r, p.Top + r), new PointF(p.Right - r, p.Top + r),
            new PointF(p.Left + r, p.Bottom - r), new PointF(p.Right - r, p.Bottom - r)
        };
        for (int k = 0; k < 4; k++)
            for (int dy = 0; dy < r; dy++)
                for (int dx = 0; dx < r; dx++)
                {
                    int x = origins[k].X + dx, y = origins[k].Y + dy;
                    float d = MathF.Sqrt(MathF.Pow(x + 0.5f - centers[k].X, 2) + MathF.Pow(y + 0.5f - centers[k].Y, 2));
                    float cov = Math.Clamp(r - d + 0.5f, 0f, 1f);
                    // Only the outer quadrant of each corner square is outside the arc.
                    bool outer = (k % 2 == 0 ? x + 0.5f < centers[k].X : x + 0.5f > centers[k].X)
                              && (k < 2 ? y + 0.5f < centers[k].Y : y + 0.5f > centers[k].Y);
                    if (!outer || cov >= 1f) continue;
                    var photoPx = img[x, y];
                    var edgePx = saved[k][dy * r + dx];
                    img[x, y] = new Rgb24(
                        ToByte(photoPx.R * cov + edgePx.R * (1 - cov)),
                        ToByte(photoPx.G * cov + edgePx.G * (1 - cov)),
                        ToByte(photoPx.B * cov + edgePx.B * (1 - cov)));
                }
    }

    private static IPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var pb = new PathBuilder();
        pb.MoveTo(new PointF(x + r, y));
        pb.LineTo(new PointF(x + w - r, y)); pb.ArcTo(r, r, 0, false, true, new PointF(x + w, y + r));
        pb.LineTo(new PointF(x + w, y + h - r)); pb.ArcTo(r, r, 0, false, true, new PointF(x + w - r, y + h));
        pb.LineTo(new PointF(x + r, y + h)); pb.ArcTo(r, r, 0, false, true, new PointF(x, y + h - r));
        pb.LineTo(new PointF(x, y + r)); pb.ArcTo(r, r, 0, false, true, new PointF(x + r, y));
        pb.CloseFigure();
        return pb.Build();
    }
}
