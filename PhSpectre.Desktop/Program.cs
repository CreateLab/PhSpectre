using System;
using Avalonia;
using PhSpectre.Avalonia;
using PhSpectre.Avalonia.Services;
using PhSpectre.Skin;
using PhSpectre.Usb;
using PhSpectre.Wpd;
using PhSpectre.Heif;

namespace PhSpectre.Desktop;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // HEIC decoding through libheif. Desktop only: Android decodes HEIC natively.
        HeifDesktopSupport.Register();

        // The skin model is loaded on first use.
        OnnxSkinSegmenter.Register(ex => AppLogger.LogError("Skin model failed", ex));

        // Windows writes recipes through the stock MTP driver (WPD); macOS and Linux use libusb.
        CameraService.TransportFactory = OperatingSystem.IsWindows()
            ? WpdCameraFactory.OpenFirstFujifilmAsync
            : LibUsbCameraFactory.OpenFirstFujifilmAsync;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}