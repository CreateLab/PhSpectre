using Avalonia.Controls;
using Avalonia.Input;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Views;

// Inspector (Style / Content / Export). All content is data-bound to SettingsViewModel — the
// old per-ComboBox population and width-based row reflow are gone with the V2 layout.
// The recipe editor is the one exception: it picks one or two field columns from the panel width.
public partial class SettingsPanel : UserControl
{
    private const double TwoColumnMinWidth = 520;

    public SettingsPanel()
    {
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            int cols = e.NewSize.Width >= TwoColumnMinWidth ? 2 : 1;
            EditGroup1.Columns = EditGroup2.Columns = EditGroup3.Columns = cols;
        };
        // Esc is the same as Cancel (which asks first when there are unsaved edits).
        RecipeEditor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is SettingsViewModel vm)
            {
                vm.CancelRecipeEditorCommand.Execute(null);
                e.Handled = true;
            }
        };
    }
}
