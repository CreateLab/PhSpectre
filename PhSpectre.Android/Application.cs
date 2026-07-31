using Android.App;
using Android.Content.Res;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using PhSpectre.Avalonia;
using PhSpectre.Avalonia.Services;

namespace PhSpectre.Android
{
    [Application]
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            FileDialogService.GallerySaver = GallerySaver.SaveAsync;

            // Read the OS's own dark/light setting directly instead of through
            // Avalonia's PlatformSettings, which isn't reliable for this on Android.
            // Must be set before base.CustomizeAppBuilder() runs framework init.
            var nightMode = Resources?.Configuration?.UiMode & UiMode.NightMask;
            App.PreferLightPalette = nightMode != UiMode.NightYes;

            return base.CustomizeAppBuilder(builder)
                .WithInterFont();
        }
    }
}