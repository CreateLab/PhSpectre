using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PhSpectre.App.Services;
using PhSpectre.App.ViewModels;

namespace PhSpectre.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();
    }

    private void WireViewModel()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this)!;
        vm.PickFolderAsync = () => FileDialogService.PickFolderAsync(topLevel);
        vm.SavePngAsync    = (name, dir) => FileDialogService.SavePngAsync(topLevel, name, dir);
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var dialog = new SettingsView { DataContext = vm.Settings };
        await dialog.ShowDialog(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not MainWindowViewModel vm) return;

        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            if (vm.SavePngAsync2Command.CanExecute(null))
                vm.SavePngAsync2Command.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            vm.SelectPreviousFile();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            vm.SelectNextFile();
            e.Handled = true;
        }
    }
}
