using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

#if !PLAY_STORE
// Only the GitHub APK checks for updates (UpdateChecker); the Play build needs no permissions at all.
[assembly: global::Android.App.UsesPermission(global::Android.Manifest.Permission.Internet)]
#endif

namespace PhSpectre.Android;

[Activity(
    Label = "PhSpectre",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}