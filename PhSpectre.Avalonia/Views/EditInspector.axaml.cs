using global::Avalonia.Controls;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Views;

public partial class EditInspector : UserControl
{
    public EditInspector()
    {
        InitializeComponent();
        ImportButton.Click += async (_, _) =>
        {
            if (DataContext is ProcessViewModel vm) await EditActions.ImportAsync(TopLevel.GetTopLevel(this), vm);
        };
        ExportAllButton.Click += async (_, _) =>
        {
            if (DataContext is ProcessViewModel vm) await EditActions.ExportAllAsync(TopLevel.GetTopLevel(this), vm);
        };
        ExportButton.Click += async (_, _) =>
        {
            if (DataContext is ProcessViewModel vm) await EditActions.ExportAsync(TopLevel.GetTopLevel(this), vm);
        };
    }
}
