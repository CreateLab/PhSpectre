using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;

namespace PhSpectre.Avalonia.Views;

public partial class SaveRow : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SaveRow, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string?> NameLabelProperty =
        AvaloniaProperty.Register<SaveRow, string?>(nameof(NameLabel));
    public static readonly StyledProperty<string?> DeleteTipProperty =
        AvaloniaProperty.Register<SaveRow, string?>(nameof(DeleteTip));
    public static readonly StyledProperty<ICommand?> SaveCommandProperty =
        AvaloniaProperty.Register<SaveRow, ICommand?>(nameof(SaveCommand));
    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<SaveRow, ICommand?>(nameof(DeleteCommand));
    public static readonly StyledProperty<bool> CanDeleteProperty =
        AvaloniaProperty.Register<SaveRow, bool>(nameof(CanDelete));

    /// <summary>The name typed for the style being saved.</summary>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>Accessible name of the text box.</summary>
    public string? NameLabel { get => GetValue(NameLabelProperty); set => SetValue(NameLabelProperty, value); }
    /// <summary>Tooltip and accessible name of the delete button.</summary>
    public string? DeleteTip { get => GetValue(DeleteTipProperty); set => SetValue(DeleteTipProperty, value); }
    public ICommand? SaveCommand { get => GetValue(SaveCommandProperty); set => SetValue(SaveCommandProperty, value); }
    public ICommand? DeleteCommand { get => GetValue(DeleteCommandProperty); set => SetValue(DeleteCommandProperty, value); }
    public bool CanDelete { get => GetValue(CanDeleteProperty); set => SetValue(CanDeleteProperty, value); }

    public SaveRow()
    {
        InitializeComponent();
        NameBox.TextChanged += (_, _) => SetCurrentValue(TextProperty, NameBox.Text);
        Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == NameLabelProperty || change.Property == DeleteTipProperty
            || change.Property == SaveCommandProperty || change.Property == DeleteCommandProperty || change.Property == CanDeleteProperty)
            Sync();
    }

    private void Sync()
    {
        if (NameBox == null) return;
        if (NameBox.Text != Text) NameBox.Text = Text;
        SaveButton.Command = SaveCommand;
        DeleteButton.Command = DeleteCommand;
        DeleteButton.IsEnabled = CanDelete;
        AutomationProperties.SetName(NameBox, NameLabel);
        AutomationProperties.SetName(DeleteButton, DeleteTip);
        ToolTip.SetTip(DeleteButton, DeleteTip);
    }
}
