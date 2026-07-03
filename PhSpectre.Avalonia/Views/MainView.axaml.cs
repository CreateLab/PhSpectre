using System;
using Avalonia.Controls;
using PhSpectre.Avalonia.Services;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();
    }

    private void WireViewModel()
    {
        if (DataContext is not MainViewModel vm) return;

        // Resolved lazily on each call, not captured once here: on Android the
        // DataContext is assigned before this control is attached to the visual
        // tree, so TopLevel.GetTopLevel(this) would still return null at this point.
        vm.PickImageAsync = () => FileDialogService.PickImageAsync(RequireTopLevel());
        vm.SavePngAsync   = (name, path) => FileDialogService.SavePngFromFileAsync(RequireTopLevel(), name, path);
        vm.Settings.OpenUrlAsync = url => RequireTopLevel().Launcher.LaunchUriAsync(new Uri(url));
    }

    private TopLevel RequireTopLevel() =>
        TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("MainView is not attached to a TopLevel.");
}
