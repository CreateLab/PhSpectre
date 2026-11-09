using System;
using Avalonia;
using PhSpectre.Avalonia;
using PhSpectre.Avalonia.Services;
using PhSpectre.Usb;
using PhSpectre.Wpd;
using PhSpectre.Heif;

namespace PhSpectre.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Registers HEIC/HEIF decode support (via native libheif) into ImageSharp's default
        // Configuration, used implicitly by every Image.Load call in PhSpectre.Services.
        // ImageLoader. Desktop-only — PhSpectre.Android never references PhSpectre.Heif,
        // since it already decodes HEIC natively through BitmapFactory (see
        // PhSpectre.Android/NativeImageDecoder.cs).
        HeifDesktopSupport.Register();

        // Writing recipes into a camera goes through the stock Windows MTP driver (WPD), so users never swap
        // drivers. macOS and Linux talk PTP to the camera directly through libusb.
        CameraService.TransportFactory = OperatingSystem.IsWindows()
            ? WpdCameraFactory.OpenFirstFujifilmAsync
            : LibUsbCameraFactory.OpenFirstFujifilmAsync;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}