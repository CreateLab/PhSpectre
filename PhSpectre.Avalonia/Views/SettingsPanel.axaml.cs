using Avalonia.Controls;

namespace PhSpectre.Avalonia.Views;

// Inspector (Style / Content / Export). All content is data-bound to SettingsViewModel — the
// old per-ComboBox population and width-based row reflow are gone with the V2 layout.
// The recipe editor (RecipeEditorView) is the one exception: it picks one or two field columns from its width.
public partial class SettingsPanel : UserControl
{
    public SettingsPanel()
    {
        InitializeComponent();
    }
}
