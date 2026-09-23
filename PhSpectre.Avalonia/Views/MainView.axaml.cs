using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using PhSpectre.Avalonia.Services;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();
        AttachedToVisualTree += (_, _) => WireSafeArea();
    }

    // Android's status bar / gesture nav bar can overlap the toolbar and bottom save bar —
    // pad the whole screen by the OS-reported safe area rather than a guessed fixed margin.
    // No-op (and harmless) on platforms without an InsetsManager, e.g. desktop previewing
    // this view, or a window manager that doesn't report insets.
    private void WireSafeArea()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var insets = topLevel?.InsetsManager;
        if (insets == null) return;

        ApplySafeArea(insets.SafeAreaPadding);
        insets.SafeAreaChanged += (_, e) => ApplySafeArea(e.SafeAreaPadding);
    }

    private void ApplySafeArea(Thickness padding) => RootPanel.Margin = padding;

    private void WireViewModel()
    {
        if (DataContext is not MainViewModel vm) return;

        // Resolved lazily on each call, not captured once here: on Android the
        // DataContext is assigned before this control is attached to the visual
        // tree, so TopLevel.GetTopLevel(this) would still return null at this point.
        vm.PickImageAsync  = () => FileDialogService.PickImageAsync(RequireTopLevel());
        vm.PickImagesAsync = () => FileDialogService.PickImagesAsync(RequireTopLevel());
        vm.SavePngAsync    = (name, path) => FileDialogService.SavePngFromFileAsync(RequireTopLevel(), name, path);
        vm.Settings.OpenUrlAsync = url => RequireTopLevel().Launcher.LaunchUriAsync(new Uri(url));
    }

    private TopLevel RequireTopLevel() =>
        TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("MainView is not attached to a TopLevel.");
}
