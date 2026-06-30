using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PhSpectre.App.Views;

public partial class SettingsView : Window
{
    public SettingsView()
    {
        InitializeComponent();

        SamplingModeBox.Items.Add("Vivid — saturated colors stand out");
        SamplingModeBox.Items.Add("Standard — most frequent colors by area");
        SamplingModeBox.Items.Add("Contrast — vivid mid-lightness colors");

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
