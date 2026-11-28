using Android.App;
using Android.Content.Res;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using PhSpectre.Avalonia;
using PhSpectre.Avalonia.Services;
using PhSpectre.Services;
using PhSpectre.Skin;

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
#if PLAY_STORE
            AppUpdateService.Enabled = false; // Play updates the app; no GitHub lookup
#endif
            FileDialogService.GallerySaver = GallerySaver.SaveAsync;
            PhSpectre.Avalonia.Services.CameraService.TransportFactory = AndroidUsbCameraTransport.OpenFirstFujifilmAsync;
            ImageLoader.FastWorkingCopyDecoder = NativeImageDecoder.TryDecodeWorkingCopy;
            OnnxSkinSegmenter.Register(ex => AppLogger.LogError("Skin model failed", ex));

            // Avalonia's PlatformSettings is unreliable for this on Android. Must run before
            // base.CustomizeAppBuilder.
            var nightMode = Resources?.Configuration?.UiMode & UiMode.NightMask;
            App.PreferLightPalette = nightMode != UiMode.NightYes;

            return base.CustomizeAppBuilder(builder)
                .WithInterFont();
        }
    }
}