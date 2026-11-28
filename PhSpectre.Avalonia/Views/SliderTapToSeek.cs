using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace PhSpectre.Avalonia.Views;

/// <summary>Tapping the track of a Slider.v2 jumps the thumb there; a double tap returns it to <c>Default</c>.</summary>
public class SliderTapToSeek
{
    private const double ThumbWidth = 22;
    private static global::System.WeakReference<Slider>? _lastPressed;

    public static readonly AttachedProperty<double> DefaultProperty =
        AvaloniaProperty.RegisterAttached<SliderTapToSeek, Slider, double>("Default");

    public static double GetDefault(Slider slider) => slider.GetValue(DefaultProperty);
    public static void SetDefault(Slider slider, double value) => slider.SetValue(DefaultProperty, value);

    public static void Register() =>
        InputElement.PointerPressedEvent.AddClassHandler<Slider>(OnPressed, RoutingStrategies.Tunnel);

    private static void OnPressed(Slider slider, PointerPressedEventArgs e)
    {
        if (!slider.Classes.Contains("v2") || !slider.IsEnabled) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;
        // Quick taps on neighbouring sliders also count as a double click.
        bool again = _lastPressed != null && _lastPressed.TryGetTarget(out var last) && last == slider;
        _lastPressed = new(slider);
        if (again && e.ClickCount >= 2)
        {
            slider.Value = GetDefault(slider);
            e.Handled = true; // keeps the second tap from starting a drag away from the default
            return;
        }
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
