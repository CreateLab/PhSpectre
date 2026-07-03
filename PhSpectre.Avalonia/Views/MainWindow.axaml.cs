using Avalonia.Controls;
using Avalonia.Input;
using PhSpectre.Avalonia.Services;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Views;

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
        vm.PickFolderAsync      = () => FileDialogService.PickFolderAsync(topLevel);
        vm.SavePngAsync         = (name, dir) => FileDialogService.SavePngAsync(topLevel, name, dir);
        vm.PickBatchFolderAsync = defaultDir => FileDialogService.PickExportFolderAsync(topLevel, defaultDir);
        vm.ShowBatchErrorsAsync = errors =>
        {
            var window = new BatchErrorsWindow { DataContext = errors };
            return window.ShowDialog(this);
        };
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
