using Avalonia.Controls;
using Avalonia.Input;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Views;

public partial class RecipeEditorView : UserControl
{
    private const double TwoColumnMinWidth = 520;

    public RecipeEditorView()
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
