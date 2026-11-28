namespace PhSpectre.Rendering;

// Positions for the six Light sliders, read from the histogram of the cropped photo before any tone or Look.
public static class AutoTone
{
    // Encoded luma the median is pulled towards.
    private const float MedianTarget = 0.46f;

    // Only Exposure, Contrast, Highlights, Shadows, Whites and Blacks are set in the result.
    public static PhotoAdjust Suggest(ImageHistogram histogram)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        float low = histogram.Percentile(0.005), dark = histogram.Percentile(0.05), median = histogram.Percentile(0.5),
            bright = histogram.Percentile(0.95), high = histogram.Percentile(0.995);
        // A single flat colour has no tones to arrange.
        if (high - low < 0.03f) return new PhotoAdjust();

        float ev = Exposure(median, high);
        float After(float v) => ToneMath.LinearToSrgb(Math.Min(ToneMath.SrgbToLinear(v) * MathF.Pow(2f, ev), 1f));
        low = After(low); dark = After(dark); bright = After(bright); high = After(high);

        return new PhotoAdjust
        {
            Exposure = ev,
            Contrast = Amount(0.6f - (bright - dark), 0.6f, 40f),
            Highlights = -Amount(bright - 0.92f, 0.08f, 50f),
            Shadows = Amount(0.08f - dark, 0.08f, 40f),
            Whites = Amount(0.96f - high, 0.2f, 40f),
            Blacks = -Amount(low - 0.04f, 0.2f, 50f),
        };
    }

    // Goes only part of the way, so night and high-key pictures keep their character.
    private static float Exposure(float median, float high)
    {
        float target = ToneMath.SrgbToLinear(MedianTarget);
        float ev = MathF.Log2(target / ToneMath.SrgbToLinear(Math.Max(median, 0.02f)));
        ev *= ev > 0 ? 0.6f : 0.4f;
        float top = ToneMath.SrgbToLinear(Math.Max(high, 0.02f));
        // A dark picture with nothing near white can come up further.
        if (median < MedianTarget && high < 0.85f)
            ev = Math.Max(ev, 0.5f * MathF.Log2(ToneMath.SrgbToLinear(0.95f) / top));
        // Brightening stops shortly after the brightest tones reach white.
        if (ev > 0) ev = Math.Min(ev, MathF.Log2(1f / top) + 0.15f);
        ev = Math.Clamp(ev, -1f, 1.5f);
        return MathF.Round(ev * 20f) / 20f;
    }

    // 0 at no excess, `max` once the excess reaches `full`.
    private static float Amount(float excess, float full, float max) =>
        MathF.Round(Math.Clamp(excess / full, 0f, 1f) * max);
}
