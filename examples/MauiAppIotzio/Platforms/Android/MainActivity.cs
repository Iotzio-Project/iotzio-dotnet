using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace MauiAppIotzio;

[Activity(Theme = "@style/Maui.SplashTheme", Exported = true, MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionMain, "android.hardware.usb.action.USB_DEVICE_ATTACHED" }, Categories = new[] { Intent.CategoryLauncher })]
[MetaData("android.hardware.usb.action.USB_DEVICE_ATTACHED", Resource = "@xml/device_filter")]
public class MainActivity : MauiAppCompatActivity
{

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        Com.Iotzio.Api.AndroidHelper.OnActivityCreate(this);
    }
}
