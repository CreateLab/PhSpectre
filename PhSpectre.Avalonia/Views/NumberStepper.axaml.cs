using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PhSpectre.Avalonia.Views;

public partial class NumberStepper : UserControl
{
    public static readonly StyledProperty<decimal> ValueProperty =
        AvaloniaProperty.Register<NumberStepper, decimal>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<decimal> MinimumProperty =
        AvaloniaProperty.Register<NumberStepper, decimal>(nameof(Minimum), -4);
    public static readonly StyledProperty<decimal> MaximumProperty =
        AvaloniaProperty.Register<NumberStepper, decimal>(nameof(Maximum), 4);
    public static readonly StyledProperty<decimal> StepProperty =
        AvaloniaProperty.Register<NumberStepper, decimal>(nameof(Step), 1);
    public static readonly StyledProperty<double> ControlHeightProperty =
        AvaloniaProperty.Register<NumberStepper, double>(nameof(ControlHeight), 40);

    public decimal Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public decimal Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public decimal Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public decimal Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public double ControlHeight { get => GetValue(ControlHeightProperty); set => SetValue(ControlHeightProperty, value); }

    private bool _syncing;

    public NumberStepper()
    {
        InitializeComponent();
        ShowValue();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty && !_syncing) ShowValue();
        else if (change.Property == ControlHeightProperty)
        {
            Input.Height = MinusButton.Height = PlusButton.Height = ControlHeight;
            MinusButton.Width = PlusButton.Width = ControlHeight;
        }
        if (change.Property == MinimumProperty || change.Property == MaximumProperty || change.Property == ValueProperty)
            UpdateButtons();
    }

    private string Format(decimal v) => v.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);

    private void ShowValue()
    {
        _syncing = true;
        Input.Text = Format(Value);
        _syncing = false;
        SetInvalid(null);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        MinusButton.IsEnabled = Value > Minimum;
        PlusButton.IsEnabled = Value < Maximum;
    }

    private void SetInvalid(string? message)
    {
        Input.Classes.Set("invalid", message != null);
        ErrorText.Text = message;
        ErrorText.IsVisible = message != null;
    }

    private void Minus_Click(object? sender, RoutedEventArgs e) => Nudge(-Step);
    private void Plus_Click(object? sender, RoutedEventArgs e) => Nudge(Step);

    private void Nudge(decimal delta)
    {
        Value = Math.Clamp(Value + delta, Minimum, Maximum);
        ShowValue();
    }

    private void Input_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        string text = (Input.Text ?? "").Trim().Replace('−', '-').Replace(',', '.');
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed))
        {
            SetInvalid($"Enter a number from {Format(Minimum)} to {Format(Maximum)}");
            return;
        }
        if (parsed < Minimum || parsed > Maximum)
        {
            SetInvalid($"Allowed range: {Format(Minimum)} to {Format(Maximum)}");
            return;
        }
        SetInvalid(null);
        _syncing = true;
        Value = parsed;
        _syncing = false;
        UpdateButtons();
    }

    // A rejected entry is reverted on blur so the field never keeps a value that isn't saved.
    private void Input_LostFocus(object? sender, RoutedEventArgs e) => ShowValue();

    private void Input_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up) { Nudge(Step); e.Handled = true; }
        else if (e.Key == Key.Down) { Nudge(-Step); e.Handled = true; }
    }
}
