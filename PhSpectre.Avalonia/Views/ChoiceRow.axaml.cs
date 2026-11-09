using System.Collections;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;

namespace PhSpectre.Avalonia.Views;

public partial class ChoiceRow : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<ChoiceRow, string?>(nameof(Label));
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ChoiceRow, IEnumerable?>(nameof(ItemsSource));
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<ChoiceRow, int>(nameof(SelectedIndex), -1, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string?> AccessibleNameProperty =
        AvaloniaProperty.Register<ChoiceRow, string?>(nameof(AccessibleName));
    public static readonly StyledProperty<double> LabelWidthProperty =
        AvaloniaProperty.Register<ChoiceRow, double>(nameof(LabelWidth), 84);

    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public IEnumerable? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    /// <summary>Name read by screen readers; the label is used when this is empty.</summary>
    public string? AccessibleName { get => GetValue(AccessibleNameProperty); set => SetValue(AccessibleNameProperty, value); }
    public double LabelWidth { get => GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }

    public ChoiceRow()
    {
        InitializeComponent();
        Combo.SelectionChanged += (_, _) => SetCurrentValue(SelectedIndexProperty, Combo.SelectedIndex);
        Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty || change.Property == ItemsSourceProperty || change.Property == SelectedIndexProperty
            || change.Property == AccessibleNameProperty || change.Property == LabelWidthProperty)
            Sync();
    }

    private void Sync()
    {
        if (Combo == null) return;
        Caption.Text = Label;
        Row.ColumnDefinitions[0].Width = new GridLength(LabelWidth);
        if (!ReferenceEquals(Combo.ItemsSource, ItemsSource)) Combo.ItemsSource = ItemsSource;
        if (Combo.SelectedIndex != SelectedIndex) Combo.SelectedIndex = SelectedIndex;
        AutomationProperties.SetName(Combo, string.IsNullOrEmpty(AccessibleName) ? Label : AccessibleName);
    }
}
