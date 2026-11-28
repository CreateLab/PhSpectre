using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.ColorGrading;

// Applies a LUT in the image's own RGB. Log-input LUTs are applied as they are.
public static class LutApplier
{
    // strength blends from the original (0) to the full LUT (1). In place, parallel by rows.
    public static void Apply(Image<Rgb24> image, CubeLut lut, float strength = 1f)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(lut);
        strength = Math.Clamp(strength, 0f, 1f);
        if (strength == 0f) return;

        const float inv = 1f / 255f;

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
