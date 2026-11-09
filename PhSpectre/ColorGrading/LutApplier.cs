using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.ColorGrading;

// Runs a CubeLut over an image's pixels. Operates in the image's own (display-referred) RGB: it is up
// to the caller to supply LUTs authored for sRGB/Rec.709 input. Log-input LUTs are applied as-is, with
// no log conversion and no warning.
public static class LutApplier
{
    // strength 0 leaves the image untouched, 1 is the full LUT, in between blends original -> graded
    // per channel (the "intensity" slider every grading tool offers). Mutates in place; rows are
    // independent so they run in parallel.
    public static void Apply(Image<Rgb24> image, CubeLut lut, float strength = 1f)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(lut);
        strength = Math.Clamp(strength, 0f, 1f);
        if (strength == 0f) return;

        const float inv = 1f / 255f;

        // ProcessPixelRows' accessor is a ref struct and can't be captured by the parallel body, so
        // go through the per-row memory handle instead (safe here: each row is touched by one thread).
        Parallel.For(0, image.Height, y =>
        {
            var row = image.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < row.Length; x++)
            {
                ref var p = ref row[x];
                float r = p.R * inv, g = p.G * inv, b = p.B * inv;
                var o = lut.Evaluate(r, g, b);

                if (strength < 1f)
                {
                    o.X = r + (o.X - r) * strength;
                    o.Y = g + (o.Y - g) * strength;
                    o.Z = b + (o.Z - b) * strength;
                }

                p.R = ToByte(o.X);
                p.G = ToByte(o.Y);
                p.B = ToByte(o.Z);
            }
        });
    }

    private static byte ToByte(float v) =>
        v <= 0f ? (byte)0 : v >= 1f ? (byte)255 : (byte)(v * 255f + 0.5f);
}
