using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PhSpectre.Avalonia.Views;

public partial class SettingsView : Window
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
