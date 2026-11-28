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

    // Pad by the system's safe area.
    private void WireSafeArea()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var insets = topLevel?.InsetsManager;
        if (insets == null) return;

        ApplySafeArea(insets.SafeAreaPadding);
        insets.SafeAreaChanged += (_, e) => ApplySafeArea(e.SafeAreaPadding);
    }

    // On Android the window already sits inside the system bars, and Avalonia reports the insets in px, which
    // doubled them. No margin there.
    private void ApplySafeArea(Thickness padding) =>
        RootPanel.Margin = OperatingSystem.IsAndroid() ? default : padding;

    // Tablet layout from 840 dp (an estimate).
    private const double WideBreakpoint = 840;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // Bottom sheet: the preview above keeps the larger part of the screen.
        SheetBorder.Height = Math.Clamp(e.NewSize.Height * 0.42, 300, 520);
        if (DataContext is MainViewModel vm) vm.IsWide = e.NewSize.Width >= WideBreakpoint;
    }

    private void WireViewModel()
    {
        if (DataContext is not MainViewModel vm) return;
        vm.IsWide = Bounds.Width >= WideBreakpoint;

        // Resolved per call: on Android the control has no TopLevel yet when the DataContext is set.
        vm.PickImageAsync  = () => FileDialogService.PickImageAsync(RequireTopLevel());
        vm.PickImagesAsync = () => FileDialogService.PickImagesAsync(RequireTopLevel());
        vm.SavePngAsync    = (name, path) => FileDialogService.SavePngFromFileAsync(RequireTopLevel(), name, path);
        vm.Settings.OpenUrlAsync = url => RequireTopLevel().Launcher.LaunchUriAsync(new Uri(url));
    }

    private TopLevel RequireTopLevel() =>
        TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("MainView is not attached to a TopLevel.");
}
