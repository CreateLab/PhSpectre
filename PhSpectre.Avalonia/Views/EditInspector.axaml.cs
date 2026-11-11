using global::Avalonia;
using global::Avalonia.Controls;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Views;

public partial class EditInspector : UserControl
{
    public static readonly StyledProperty<bool> RailProperty = AvaloniaProperty.Register<EditInspector, bool>(nameof(Rail), true);
    /// <summary>True: the tools live in the rail beside the photo (desktop, tablet). False: one row on top (phone).</summary>
    public bool Rail { get => GetValue(RailProperty); set => SetValue(RailProperty, value); }

    public EditInspector()
    {
        InitializeComponent();
        ImportButton.Click += async (_, _) =>
        {
            if (DataContext is ProcessViewModel vm) await EditActions.ImportAsync(TopLevel.GetTopLevel(this), vm);
        };
        ExportAllButton.Click += async (_, _) =>
        {
            if (DataContext is not ProcessViewModel vm) return;
            // One Export tab for every frame; Card / Recipe card are saved by the card renderer behind the same buttons.
            if (vm.Settings.FrameIsCardType) { if (vm.Settings.CardSaveAllCommand?.CanExecute(null) == true) vm.Settings.CardSaveAllCommand.Execute(null); }
            else await EditActions.ExportAllAsync(TopLevel.GetTopLevel(this), vm);
        };
        ExportButton.Click += async (_, _) =>
        {
            if (DataContext is not ProcessViewModel vm) return;
            if (vm.Settings.FrameIsCardType) { if (vm.Settings.CardSaveCommand?.CanExecute(null) == true) vm.Settings.CardSaveCommand.Execute(null); }
            else await EditActions.ExportAsync(TopLevel.GetTopLevel(this), vm);
        };
    }
}
