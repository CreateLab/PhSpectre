using System.Numerics;

namespace PhSpectre.ColorGrading;

// An Adobe/Resolve ".cube" lookup table, held as float RGB triples. Supports both the 3D form
// (LUT_3D_SIZE — a cube of N^3 colors, red varying fastest) and the 1D form (LUT_1D_SIZE — an
// independent curve per channel). The spec forbids files carrying both, so exactly one applies.
//
// Evaluate() is the whole runtime contract: feed it an RGB triple in 0..1 *image* space and get the
// graded RGB back (unclamped — the caller decides how to quantize). 3D tables use tetrahedral
// interpolation rather than trilinear: it follows the cube's neutral diagonal exactly and avoids the
// color fringing trilinear shows on coarse grids (the freely available 13^3 film CLUTs especially).
public sealed class CubeLut
{
    public string? Title { get; }
    public bool Is3D { get; }
    public int Size { get; }
    public Vector3 DomainMin { get; }
    public Vector3 DomainMax { get; }

    // Interleaved RGB. 3D: index = (r + g*N + b*N*N) * 3. 1D: index = i * 3.
    private readonly float[] _data;

    internal CubeLut(string? title, bool is3D, int size, Vector3 domainMin, Vector3 domainMax, float[] data)
    {
        Title = title;
        Is3D = is3D;
        Size = size;
        DomainMin = domainMin;
        DomainMax = domainMax;
        _data = data;
    }

    public static CubeLut Load(string path)
    {
        using var reader = new StreamReader(path);
        return CubeLutParser.Parse(reader);
    }

    public static CubeLut Parse(string text)
    {
        using var reader = new StringReader(text);
        return CubeLutParser.Parse(reader);
    }

    public Vector3 Evaluate(float r, float g, float b)
    {
        // Map image space 0..1 through the table's declared input domain, then clamp: values outside
        // the domain take the edge entry, which is what every grading tool does.
        float x = Normalize(r, DomainMin.X, DomainMax.X);
        float y = Normalize(g, DomainMin.Y, DomainMax.Y);
        float z = Normalize(b, DomainMin.Z, DomainMax.Z);
        return Is3D ? Tetrahedral(x, y, z) : Linear1D(x, y, z);
    }

    private static float Normalize(float v, float min, float max)
    {
        float range = max - min;
        float t = range > 0f ? (v - min) / range : 0f;
        return t < 0f ? 0f : t > 1f ? 1f : t;
    }

    private Vector3 At(int r, int g, int b)
    {
        int i = (r + g * Size + b * Size * Size) * 3;
        return new Vector3(_data[i], _data[i + 1], _data[i + 2]);
    }

    private Vector3 Tetrahedral(float x, float y, float z)
    {
        int max = Size - 1;
        float fx = x * max, fy = y * max, fz = z * max;
        // Clamp the base cell to N-2 so x == 1.0 lands on the last cell with frac 1, not out of range.
        int ix = Math.Min((int)fx, max - 1), iy = Math.Min((int)fy, max - 1), iz = Math.Min((int)fz, max - 1);
        float dx = fx - ix, dy = fy - iy, dz = fz - iz;

        var c000 = At(ix, iy, iz);
        var c111 = At(ix + 1, iy + 1, iz + 1);

        // The cell is split into six tetrahedra along its main diagonal; which one holds the point is
        // decided by the ordering of the three fractional offsets. Walk 000 -> ... -> 111 along the
        // axes in descending-offset order, adding each edge scaled by its offset.
        if (dx >= dy)
        {
            if (dy >= dz)
            {
                var c100 = At(ix + 1, iy, iz); var c110 = At(ix + 1, iy + 1, iz);
                return c000 + dx * (c100 - c000) + dy * (c110 - c100) + dz * (c111 - c110);
            }
            if (dx >= dz)
            {
                var c100 = At(ix + 1, iy, iz); var c101 = At(ix + 1, iy, iz + 1);
                return c000 + dx * (c100 - c000) + dz * (c101 - c100) + dy * (c111 - c101);
            }
            {
                var c001 = At(ix, iy, iz + 1); var c101 = At(ix + 1, iy, iz + 1);
                return c000 + dz * (c001 - c000) + dx * (c101 - c001) + dy * (c111 - c101);
            }
        }
        if (dz >= dy)
        {
            var c001 = At(ix, iy, iz + 1); var c011 = At(ix, iy + 1, iz + 1);
            return c000 + dz * (c001 - c000) + dy * (c011 - c001) + dx * (c111 - c011);
        }
        if (dz >= dx)
        {
            var c010 = At(ix, iy + 1, iz); var c011 = At(ix, iy + 1, iz + 1);
            return c000 + dy * (c010 - c000) + dz * (c011 - c010) + dx * (c111 - c011);
        }
        {
            var c010 = At(ix, iy + 1, iz); var c110 = At(ix + 1, iy + 1, iz);
            return c000 + dy * (c010 - c000) + dx * (c110 - c010) + dz * (c111 - c110);
        }
    }

    private Vector3 Linear1D(float x, float y, float z) =>
        new(Curve(x, 0), Curve(y, 1), Curve(z, 2));

    private float Curve(float t, int channel)
    {
        int max = Size - 1;
        float f = t * max;
        int i = Math.Min((int)f, max - 1);
        float d = f - i;
        float a = _data[i * 3 + channel];
        float b = _data[(i + 1) * 3 + channel];
        return a + (b - a) * d;
    }
}
