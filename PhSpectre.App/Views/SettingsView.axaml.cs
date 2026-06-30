using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PhSpectre.App.Views;

public partial class SettingsView : Window
{
    public SettingsView()
    {
        InitializeComponent();

        ColorCountBox.Items.Add("Auto");
        for (int i = 3; i <= 8; i++)
            ColorCountBox.Items.Add(i.ToString());

        MetaVerbosityBox.Items.Add("None");
        MetaVerbosityBox.Items.Add("Short");
        MetaVerbosityBox.Items.Add("Default");
        MetaVerbosityBox.Items.Add("Detail");
        MetaVerbosityBox.Items.Add("Full");

        MetaStyleBox.Items.Add("Film strip");
        MetaStyleBox.Items.Add("Overlay");
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
