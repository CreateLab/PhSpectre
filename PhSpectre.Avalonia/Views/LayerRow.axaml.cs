using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;

namespace PhSpectre.Avalonia.Views;

public partial class LayerRow : UserControl
{
    public static readonly StyledProperty<bool> IsOnProperty =
        AvaloniaProperty.Register<LayerRow, bool>(nameof(IsOn), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<LayerRow, string?>(nameof(Kind));
    public static readonly StyledProperty<string?> LayerNameProperty =
        AvaloniaProperty.Register<LayerRow, string?>(nameof(LayerName));
    public static readonly StyledProperty<ICommand?> ClearCommandProperty =
        AvaloniaProperty.Register<LayerRow, ICommand?>(nameof(ClearCommand));

    public bool IsOn { get => GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
    /// <summary>What kind of layer it is: "Preset", "LUT".</summary>
    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string? LayerName { get => GetValue(LayerNameProperty); set => SetValue(LayerNameProperty, value); }
    public ICommand? ClearCommand { get => GetValue(ClearCommandProperty); set => SetValue(ClearCommandProperty, value); }

    public LayerRow()
    {
        InitializeComponent();
        Toggle.IsCheckedChanged += (_, _) => SetCurrentValue(IsOnProperty, Toggle.IsChecked == true);
        Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOnProperty || change.Property == KindProperty
            || change.Property == LayerNameProperty || change.Property == ClearCommandProperty)
            Sync();
    }

    private void Sync()
    {
        if (Toggle == null) return;
        if (Toggle.IsChecked != IsOn) Toggle.IsChecked = IsOn;
        KindText.Text = Kind;
        NameText.Text = LayerName;
        Clear.Command = ClearCommand;
        AutomationProperties.SetName(Toggle, $"{Kind} on or off");
        AutomationProperties.SetName(Clear, $"Remove {Kind}");
        ToolTip.SetTip(Clear, $"Remove {Kind}");
    }
}
