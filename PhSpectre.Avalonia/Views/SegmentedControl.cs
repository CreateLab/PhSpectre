using System;
using Avalonia;
using Avalonia.Controls;

namespace PhSpectre.Avalonia.Views;

/// <summary>
/// The app's segmented switch (a ListBox styled by <c>ListBox.seg</c>). Either give it <see cref="Labels"/>
/// ("PNG|JPEG") or put ListBoxItems inside when a segment needs an icon, IsVisible or IsEnabled.
/// Bind <c>SelectedIndex</c> as usual.
/// </summary>
public class SegmentedControl : ListBox
{
    public static readonly StyledProperty<string?> LabelsProperty =
        AvaloniaProperty.Register<SegmentedControl, string?>(nameof(Labels));

    /// <summary>Segment captions separated by '|'.</summary>
    public string? Labels { get => GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }

    protected override Type StyleKeyOverride => typeof(ListBox);

    public SegmentedControl() => Classes.Add("seg");

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LabelsProperty) return;
        Items.Clear();
        if (string.IsNullOrEmpty(Labels)) return;
        foreach (var label in Labels.Split('|')) Items.Add(new ListBoxItem { Content = label });
    }
}

/// <summary>Result | Original switch shown over a photo; <c>SelectedIndex</c> 0 = result, 1 = original.</summary>
public class CompareSwitch : SegmentedControl
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    public CompareSwitch()
    {
        Labels = "Result|Original";
        Width = 190;
    }
}
