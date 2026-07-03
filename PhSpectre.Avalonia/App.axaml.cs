using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using PhSpectre.Avalonia.ViewModels;
using PhSpectre.Avalonia.Views;

namespace PhSpectre.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Desktop always runs light, regardless of the Windows theme — the dark
            // palette wasn't working out there. Both the theme variant (so FluentTheme's
            // own default control colors switch too) and our own color tokens need to
            // flip; this merges a light override on top of Colors.axaml's dark tokens,
            // same keys, so it applies with no XAML changes anywhere else. Must happen
            // before MainWindow is constructed, since StaticResource resolves once.
            RequestedThemeVariant = ThemeVariant.Light;
            var lightColors = AvaloniaXamlLoader.Load(
                new Uri("avares://PhSpectre.Avalonia/Styles/Colors.Light.axaml"));
            if (lightColors is IResourceProvider lightColorsResources)
                Resources.MergedDictionaries.Add(lightColorsResources);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory =
                () => new MainView { DataContext = new MainViewModel() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}