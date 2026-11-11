using System.Numerics;

namespace PhSpectre.Rendering;

// Per-pixel maths of the Edit sliders (colour in 0..1, sRGB). Same curves as the Lightroom preset grader,
// so a slider here feels like the same slider there.
internal static class ToneMath
{
    public static readonly float[] Decode = BuildDecode();
    private static readonly float[] EncodeTable = BuildEncode();
    private const int EncodeSize = 4096;

    private static float[] BuildDecode()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = SrgbToLinear(i / 255f);
        return t;
    }

    private static float[] BuildEncode()
    {
        var t = new float[EncodeSize];
        for (int i = 0; i < EncodeSize; i++) t[i] = LinearToSrgb(i / (float)(EncodeSize - 1));
        return t;
    }

    public static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    public static float LinearToSrgb(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;

    public static float Encode(float linear) => EncodeTable[(int)(Math.Clamp(linear, 0f, 1f) * (EncodeSize - 1) + 0.5f)];

    public static float Luma(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    public static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Slider values are -100..100. Positive temperature = warmer (more red, less blue), positive tint = magenta.
    // Normalised so the luma of white stays put.
    public static Vector3 WhiteBalanceGain(float temperature, float tint)
    {
        if (temperature == 0f && tint == 0f) return Vector3.One;
        float warmth = temperature / 100f * 0.6f, t = tint / 100f * 0.3f;
        const float k = 0.5f;
        var g = new Vector3(MathF.Pow(2f, warmth * k), MathF.Pow(2f, -t), MathF.Pow(2f, -warmth * k));
        return g / Luma(g);
    }

    // Contrast / highlights / shadows / whites / blacks (each -1..1) act on luma; colour follows by scaling.
    public static Vector3 Tone(Vector3 c, float contrast, float highlights, float shadows, float whites, float blacks)
    {
        float l = Math.Clamp(Luma(c), 0f, 1f);
        float n = l;
        n = Push(n, highlights, Smooth(0.5f, 1f, l), 0.35f);
        n = Push(n, shadows, 1f - Smooth(0f, 0.5f, l), 0.35f);
        n = Push(n, whites, Smooth(0.7f, 1f, l), 0.2f);
        n = Push(n, blacks, 1f - Smooth(0f, 0.3f, l), 0.2f);
        if (contrast > 0) n += (Smooth(0f, 1f, n) - n) * contrast;
        else if (contrast < 0) n = 0.5f + (n - 0.5f) * (1f + contrast * 0.5f);
        return l > 1e-4f ? c * MathF.Min(n / l, 4f) : c + new Vector3(n - l);
    }

    private static float Push(float l, float amt, float w, float gain) =>
        amt == 0f ? l : l + amt * gain * w * (amt > 0 ? 1f - l : l);

    // Saturation and vibrance (each -1..1); vibrance moves the dull colours more than the vivid ones.
    public static Vector3 VibranceSaturation(Vector3 c, float saturation, float vibrance)
    {
        float gray = Luma(c);
        float chroma = MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float scale = (1f + saturation) * (1f + vibrance * (1f - Math.Clamp(chroma, 0f, 1f)));
        return new Vector3(gray) + (c - new Vector3(gray)) * MathF.Max(scale, 0f);
    }

    // Cheap deterministic noise in 0..1 (same picture every run).
    public static float Hash(int x, int y)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    public static byte ToByte(float v) => v <= 0f ? (byte)0 : v >= 1f ? (byte)255 : (byte)(v * 255f + 0.5f);
}
