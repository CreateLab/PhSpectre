using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace PhSpectre.Avalonia.Views;

/// <summary>
/// Tapping the track of a Slider.v2 jumps the thumb there (instead of paging by LargeChange),
/// and the drag then continues from the new position as usual.
/// </summary>
internal static class SliderTapToSeek
{
    private const double ThumbWidth = 22;

    public static void Register() =>
        InputElement.PointerPressedEvent.AddClassHandler<Slider>(OnPressed, RoutingStrategies.Tunnel);

    private static void OnPressed(Slider slider, PointerPressedEventArgs e)
    {
        if (!slider.Classes.Contains("v2") || !slider.IsEnabled) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Thumb>(true) != null) return; // dragging the knob itself
        if (slider.Orientation != global::Avalonia.Layout.Orientation.Horizontal) return;

        double width = slider.Bounds.Width - ThumbWidth - 20; // template has 10px side margins
        if (width <= 0) return;
        double x = e.GetPosition(slider).X - 10 - ThumbWidth / 2;
        double t = global::System.Math.Clamp(x / width, 0, 1);
        if (slider.IsDirectionReversed) t = 1 - t;
        double value = slider.Minimum + t * (slider.Maximum - slider.Minimum);
        if (slider.IsSnapToTickEnabled && slider.TickFrequency > 0)
            value = slider.Minimum + global::System.Math.Round((value - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency;
        slider.Value = value;
    }
}
