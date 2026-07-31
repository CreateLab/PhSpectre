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
            // Desktop always runs light, regardless of the Windows theme — the dark
            // palette wasn't working out there. Must happen before MainWindow is
            // constructed, since StaticResource resolves once.
            ApplyLightPalette();

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
            // Mobile has no in-app light/dark toggle, so match the phone's system theme
            // once at startup (no need to react if it changes mid-session).
            if (PreferLightPalette)
                ApplyLightPalette();

            singleViewFactoryApplicationLifetime.MainViewFactory =
                () => new MainView { DataContext = new MainViewModel() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            if (PreferLightPalette)
                ApplyLightPalette();

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

    // Flips the theme variant (so FluentTheme's own default control colors switch too)
    // and merges a light override on top of Colors.axaml's dark tokens, same keys, so
    // it applies with no XAML changes anywhere else.
    private void ApplyLightPalette()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        var lightColors = AvaloniaXamlLoader.Load(
            new Uri("avares://PhSpectre.Avalonia/Styles/Colors.Light.axaml"));
        if (lightColors is IResourceProvider lightColorsResources)
            Resources.MergedDictionaries.Add(lightColorsResources);
    }
}
