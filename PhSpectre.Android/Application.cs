using Android.App;
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

            return base.CustomizeAppBuilder(builder)
                .WithInterFont();
        }
    }
}