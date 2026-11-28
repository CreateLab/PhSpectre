namespace PhSpectre.Rendering;

// Grain sized relative to the photo, so preview and export match. Deterministic per pixel.
internal static class FilmGrain
{
    // Lightroom's default for GrainSize.
    public const float DefaultSize = 25f;

    public static float CellSize(int photoLongSide, float size = DefaultSize) =>
        MathF.Max(1f, photoLongSide / 1600f * (0.5f + Math.Clamp(size, 0f, 100f) / 50f));

    // About -1..1. Grains bigger than a pixel are interpolated between random values on a coarser lattice.
    public static float Sample(int x, int y, float cell)
    {
        if (cell <= 1f) return (ToneMath.Hash(x, y) - 0.5f) * 2f;
        float fx = x / cell, fy = y / cell;
        int ix = (int)fx, iy = (int)fy;
        float tx = fx - ix, ty = fy - iy;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        float a = ToneMath.Hash(ix, iy), b = ToneMath.Hash(ix + 1, iy);
        float c = ToneMath.Hash(ix, iy + 1), d = ToneMath.Hash(ix + 1, iy + 1);
        float top = a + (b - a) * tx, bottom = c + (d - c) * tx;
        // Gain restores the contrast lost to interpolation.
        return (top + (bottom - top) * ty - 0.5f) * 2f * 1.35f;
    }
}
