using PhSpectre.Localization;
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using PhSpectre.Avalonia.Services;
using PhSpectre.Avalonia.ViewModels;
using PhSpectre.Avalonia.Views;

namespace PhSpectre.Avalonia;

public partial class App : Application
{
    // Set by the Android head before framework init: Avalonia's system theme detection is unreliable there at this
    // point.
    public static bool PreferLightPalette;

    public override void Initialize()
    {
        // Before any XAML or view model reads a string.
        Loc.Use(SettingsStorage.Load().Language);
        AvaloniaXamlLoader.Load(this);
        Views.SliderTapToSeek.Register();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplyAppearance(SettingsStorage.Load().Appearance);

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                AppLogger.LogError("Unhandled exception", e.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLogger.LogError("Unobserved task exception", e.Exception);
                e.SetObserved();
            };

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            ApplyAppearance(SettingsStorage.Load().Appearance);

            singleViewFactoryApplicationLifetime.MainViewFactory =
                () => new MainView { DataContext = new MainViewModel() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            ApplyAppearance(SettingsStorage.Load().Appearance);

            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();

        // Never blocks startup; CheckAsync stays silent on dev builds, network errors and during the cooldown.
        _ = AppUpdateService.Instance.CheckAsync();
    }

    // App chrome only (the exported card has its own theme). Colours come through DynamicResource, so the open
    // window restyles in place. On Android "System" follows PreferLightPalette.
    public static void ApplyAppearance(AppAppearance appearance)
    {
        if (Current is not { } app) return;
        app.RequestedThemeVariant = appearance switch
        {
            AppAppearance.Light => ThemeVariant.Light,
            AppAppearance.Dark => ThemeVariant.Dark,
            _ => OperatingSystem.IsAndroid()
                ? (PreferLightPalette ? ThemeVariant.Light : ThemeVariant.Dark)
                : ThemeVariant.Default
        };
    }
}
