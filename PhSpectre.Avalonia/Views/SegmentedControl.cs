using PhSpectre.Localization;
using System;
using Avalonia;
using Avalonia.Controls;

namespace PhSpectre.Avalonia.Views;

/// <summary>A segmented switch: set <see cref="Labels"/> ("PNG|JPEG") or put ListBoxItems inside.</summary>
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

/// <summary>Result | Original switch: SelectedIndex 0 = result, 1 = original.</summary>
public class CompareSwitch : SegmentedControl
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    public CompareSwitch()
    {
        Labels = Loc.T("Result|Original");
        Width = 190;
    }
}
