using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Rendering;

/// <summary>Finds skin in a photo. The app registers an implementation in <see cref="SkinRetouch"/>.</summary>
public interface ISkinSegmenter
{
    /// <summary>Null when the model failed to load.</summary>
    SkinMask? Segment(Image<Rgb24> photo);
}

// Skin probability on a small grid over the whole photo. Left, Top, Across and Down locate the picture being
// retouched within that photo, as shares of its size.
public sealed class SkinMask
{
    private readonly float[] _values;

    public int Width { get; }
    public int Height { get; }
    /// <summary>Share of the whole photo that is skin, 0..1. Sets the smoothing radius.</summary>
    public float Coverage { get; }
    public double Left { get; private init; }
    public double Top { get; private init; }
    public double Across { get; private init; } = 1;
    public double Down { get; private init; } = 1;

    public SkinMask(float[] values, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (width <= 0 || height <= 0 || values.Length != width * height)
            throw new ArgumentException("The mask does not match its size.", nameof(values));
        _values = values;
        Width = width; Height = height;
        float sum = 0f;
        foreach (var v in values) sum += Math.Clamp(v, 0f, 1f);
        Coverage = sum / values.Length;
    }

    private SkinMask(SkinMask whole)
    {
        _values = whole._values;
        Width = whole.Width; Height = whole.Height; Coverage = whole.Coverage;
    }

    /// <summary>The mask for a rectangle cut out of a photo of the given size.</summary>
    public SkinMask Piece(Rectangle rect, int photoWidth, int photoHeight) => new(this)
    {
        Left = (double)rect.X / photoWidth, Top = (double)rect.Y / photoHeight,
        Across = (double)rect.Width / photoWidth, Down = (double)rect.Height / photoHeight
    };

    public float At(double x, double y)
    {
        double fx = Math.Clamp((Left + x * Across) * Width - 0.5, 0, Width - 1);
        double fy = Math.Clamp((Top + y * Down) * Height - 0.5, 0, Height - 1);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        float top = _values[y0 * Width + x0] + (_values[y0 * Width + x1] - _values[y0 * Width + x0]) * tx;
        float bottom = _values[y1 * Width + x0] + (_values[y1 * Width + x1] - _values[y1 * Width + x0]) * tx;
        return top + (bottom - top) * ty;
    }
}
