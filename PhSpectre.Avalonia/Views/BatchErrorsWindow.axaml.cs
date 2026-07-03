using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PhSpectre.Avalonia.Views;

public partial class BatchErrorsWindow : Window
{
    public BatchErrorsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
