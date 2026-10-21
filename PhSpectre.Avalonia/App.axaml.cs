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
    // Set by the Android host (PhSpectre.Android/Application.cs), read directly from
    // Android's Configuration.UiMode, before framework init runs. Avalonia's own
    // PlatformSettings-based system-theme detection isn't reliable at this point on
    // Android (seen returning Light regardless of the phone's actual setting), so the
    // Android head reads the OS config itself instead of us querying PlatformSettings
    // here. Other platforms ignore this.
    public static bool PreferLightPalette;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
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

        // Fire-and-forget: never blocks startup, and CheckAsync() itself no-ops
        // silently on dev builds, network failures, or when the daily cooldown hasn't
        // elapsed yet.
        _ = AppUpdateService.Instance.CheckAsync();
    }

    // App theme (System / Light / Dark). Tokens live in Colors.axaml's ThemeDictionaries and are
    // consumed through DynamicResource, so flipping RequestedThemeVariant restyles the open
    // window in place. System on Android uses the phone's UI mode read by the Android head
    // (PreferLightPalette) because Avalonia's own system-theme detection is unreliable there.
    // This is the app chrome only — the exported card has its own theme (Style → Card).
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
