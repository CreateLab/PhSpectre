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

    // On Android the activity window is already laid out inside the system bars (the view starts 94 px
    // below the top and ends 45 px above the bottom), and Avalonia reports the same insets in SafeAreaPadding
    // — in px, which then got applied as dp as well. Net effect: bars ~4x too thick. So on Android the
    // window already did the job and no extra margin is wanted; other platforms still use the reported area.
    private void ApplySafeArea(Thickness padding) =>
        RootPanel.Margin = OperatingSystem.IsAndroid() ? default : padding;

    // Tablet layout (inspector as a right-hand column) from 840 dp up; below that the inspector
    // is a bottom sheet. Breakpoint is an estimate — confirm on real devices.
    private const double WideBreakpoint = 840;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // Bottom sheet: ~60% of the screen (the photo strip hides while it is open); the preview above keeps the rest.
        SheetBorder.Height = Math.Clamp(e.NewSize.Height * 0.5, 320, 560);
        if (DataContext is MainViewModel vm) vm.IsWide = e.NewSize.Width >= WideBreakpoint;
    }

    private void WireViewModel()
    {
        if (DataContext is not MainViewModel vm) return;
        vm.IsWide = Bounds.Width >= WideBreakpoint;

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
