using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;

namespace PhSpectre.Avalonia.Views;

/// <summary>Square toolbar button with one icon (style class <c>icon-toolbar</c>). Set Width/Height for the hit area.</summary>
public class IconButton : Button
{
    public static readonly StyledProperty<MaterialIconKind> IconProperty =
        AvaloniaProperty.Register<IconButton, MaterialIconKind>(nameof(Icon));
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconButton, double>(nameof(IconSize), 18);

    public MaterialIconKind Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    protected override Type StyleKeyOverride => typeof(Button);

    public IconButton()
    {
        Classes.Add("icon-toolbar");
        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty || change.Property == IconSizeProperty) Rebuild();
    }

    private void Rebuild() => Content = new MaterialIcon { Kind = Icon, Width = IconSize, Height = IconSize };
}

/// <summary>Button with an optional leading icon and a caption. Pick the look with Classes (primary, cta, secondary, ghost).</summary>
public class ActionButton : Button
{
    public static readonly StyledProperty<MaterialIconKind?> IconProperty =
        AvaloniaProperty.Register<ActionButton, MaterialIconKind?>(nameof(Icon));
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ActionButton, string?>(nameof(Text));
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<ActionButton, double>(nameof(IconSize), 18);

    public MaterialIconKind? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    protected override Type StyleKeyOverride => typeof(Button);

    public ActionButton() => Rebuild();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty || change.Property == TextProperty || change.Property == IconSizeProperty) Rebuild();
    }

    private void Rebuild()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (Icon is { } kind)
            row.Children.Add(new MaterialIcon { Kind = kind, Width = IconSize, Height = IconSize, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = Text, VerticalAlignment = VerticalAlignment.Center });
        Content = row;
    }
}

/// <summary>Colour chip that applies its colour through Command (the hex is the command parameter).</summary>
public class ColorSwatch : Button
{
    public static readonly StyledProperty<string?> ColorProperty =
        AvaloniaProperty.Register<ColorSwatch, string?>(nameof(Color));

    /// <summary>"#RRGGBB"; also passed as CommandParameter.</summary>
    public string? Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    protected override Type StyleKeyOverride => typeof(Button);

    public ColorSwatch()
    {
        Width = 28;
        Height = 28;
        BorderThickness = new Thickness(1);
        this.Bind(BorderBrushProperty, this.GetResourceObservable("FieldBorderBrush"));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ColorProperty) return;
        CommandParameter = Color;
        if (Color != null && global::Avalonia.Media.Color.TryParse(Color, out var c)) Background = new SolidColorBrush(c);
    }
}
