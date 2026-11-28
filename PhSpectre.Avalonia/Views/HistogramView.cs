using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PhSpectre.Rendering;

namespace PhSpectre.Avalonia.Views;

// Red, green and blue areas with a luma outline; a triangle in a top corner lights up when that end clips.
public sealed class HistogramView : Control
{
    private const double ClipWarning = 0.005;

    public static readonly StyledProperty<ImageHistogram?> DataProperty =
        AvaloniaProperty.Register<HistogramView, ImageHistogram?>(nameof(Data));

    private static readonly IBrush RedBrush = new SolidColorBrush(Color.FromArgb(110, 255, 70, 70));
    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.FromArgb(110, 70, 220, 90));
    private static readonly IBrush BlueBrush = new SolidColorBrush(Color.FromArgb(110, 70, 130, 255));
    private static readonly IPen LumaPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)));
    private static readonly IBrush ClipOffBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
    private static readonly IBrush ClipOnBrush = new SolidColorBrush(Color.FromRgb(255, 190, 40));

    static HistogramView() => AffectsRender<HistogramView>(DataProperty);

    public ImageHistogram? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Data is not { Total: > 0 } h) return;
        double w = Bounds.Width, height = Bounds.Height;

        // The end bins are left out of the scale: one clipped area would flatten the rest.
        int peak = 1;
        for (int i = 1; i < 255; i++)
            peak = Math.Max(peak, Math.Max(h.Luma[i], Math.Max(h.Red[i], Math.Max(h.Green[i], h.Blue[i]))));

        context.DrawGeometry(RedBrush, null, Curve(h.Red, peak, w, height, filled: true));
        context.DrawGeometry(GreenBrush, null, Curve(h.Green, peak, w, height, filled: true));
        context.DrawGeometry(BlueBrush, null, Curve(h.Blue, peak, w, height, filled: true));
        context.DrawGeometry(null, LumaPen, Curve(h.Luma, peak, w, height, filled: false));

        context.DrawGeometry(h.ShadowClipping > ClipWarning ? ClipOnBrush : ClipOffBrush, null,
            Triangle(new Point(0, 0), new Point(9, 0), new Point(0, 9)));
        context.DrawGeometry(h.HighlightClipping > ClipWarning ? ClipOnBrush : ClipOffBrush, null,
            Triangle(new Point(w, 0), new Point(w - 9, 0), new Point(w, 9)));
    }

    private static StreamGeometry Curve(IReadOnlyList<int> bins, int peak, double w, double h, bool filled)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        Point At(int i) => new(i / 255.0 * w, h - Math.Min(1.0, (double)bins[i] / peak) * h);
        g.BeginFigure(filled ? new Point(0, h) : At(0), filled);
        for (int i = filled ? 0 : 1; i < 256; i++) g.LineTo(At(i));
        if (filled) g.LineTo(new Point(w, h));
        g.EndFigure(filled);
        return geometry;
    }

    private static StreamGeometry Triangle(Point a, Point b, Point c)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(a, true);
        g.LineTo(b);
        g.LineTo(c);
        g.EndFigure(true);
        return geometry;
    }
}
