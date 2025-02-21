# .NET MAUI Examples

This .NET MAUI App example can run on Windows and Android.

## Running the Examples

To run the desired example on Windows:

1. Open the solution in Visual Studio.

2. In the Solution Explorer, navigate to the project you want to run.

3. Right-click on the project and select "Set as Startup Project".

4. In the toolbar, select "Windows Machine" as the debug target.

5. Press F5 or click on the "Start" button to run the project.

To run the desired example on Android:

1. Open the solution in Visual Studio.

2. In the Solution Explorer, navigate to the project you want to run.

3. Right-click on the project and select "Set as Startup Project".

4. Connect your Android device via USB or start an Android emulator.

5. In the toolbar, select your Android device or emulator as the debug target.

6. Press F5 or click on the "Start" button to run the project.

## Requirements

- .NET SDK 8.0 installed

- Visual Studio 2022 (version 17.3 or later) installed with the .NET Multi-platform App UI (MAUI) workload

- For Android:

    - Android SDK installed

    - Android device or emulator

## Notes Android Development

Android requires some extra steps in order for Iotzio devices and its API to work properly.

### MainActivity

The Main Activity must be extended as shown below. Make sure to add the `Exported = true` property, add `IntentFilter` and `MetaData` attribute.

```
[Activity(Theme = "@style/Maui.SplashTheme", Exported = true, MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigurationChanges.Orientation | ConfigurationChanges.UiMode | ConfigurationChanges.ScreenLayout | ConfigurationChanges.SmallestScreenSize | ConfigurationChanges.Density)]
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
```

Always call `Com.Iotzio.Api.AndroidHelper.OnActivityCreate(this);` in the `OnCreate` method.

### Device Filter

Create or extend `Resources/xml/device_filter.xml` like below:

```
<?xml version="1.0" encoding="utf-8"?>

<resources>
    <usb-device vendor-id="11914" product-id="15"/>
</resources>
```