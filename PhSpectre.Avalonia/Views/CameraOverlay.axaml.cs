using global::Avalonia.Controls;
using global::Avalonia.Input.Platform;
using global::Avalonia.Interactivity;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Views;

public partial class CameraOverlay : UserControl
{
    public CameraOverlay()
    {
        InitializeComponent();
    }

    private async void CopyDetails_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            await CopyAsync(vm.Camera.ErrorDetails);
    }



    private async System.Threading.Tasks.Task CopyAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null && !string.IsNullOrEmpty(text))
            await clipboard.SetTextAsync(text);
    }
}
