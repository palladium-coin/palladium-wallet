using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Avalonia.Android;
using PalladiumWallet.App.Services;
using AvaloniaApp = PalladiumWallet.App.App;

namespace PalladiumWallet.Mobile;

[Activity(
    Label = "Palladium Wallet",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    internal const int ScanRequestCode = 9001;
    internal static TaskCompletionSource<string?>? ScanTcs;
    internal static MainActivity? Current;
    private bool _wasPaused;
    private bool _night;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Set before the app starts so the "System" theme is right on the first frame.
        _night = (Resources?.Configuration?.UiMode & UiMode.NightMask) == UiMode.NightYes;
        PlatformServices.IsSystemDark = () => _night;
        base.OnCreate(savedInstanceState);
        Current = this;
    }

    // The activity declares UiMode in ConfigurationChanges (recreating it would drop an unlocked wallet), so the
    // system tells it, and only it, when the person switches between light and dark: pass that on to the theme.
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        // Read from the configuration the system just delivered, not from Resources, which may still hold the old one.
        _night = (newConfig.UiMode & UiMode.NightMask) == UiMode.NightYes;
        AvaloniaApp.MainViewModel?.RefreshSystemTheme();
    }

    protected override void OnPause()
    {
        base.OnPause();
        _wasPaused = true;
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (!_wasPaused) return;
        _wasPaused = false;
        // The TCP socket can die silently while the screen was off/locked (Doze,
        // mobile radio suspend, NAT timeout) without the app ever observing the
        // failure. Force an immediate health check instead of waiting for the next
        // 20s keep-alive tick, which may itself have been suspended for longer than
        // the lock.
        if (AvaloniaApp.MainViewModel is { } vm)
            _ = vm.CheckConnectionOnResumeAsync();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == ScanRequestCode)
        {
            var text = resultCode == Result.Ok ? data?.GetStringExtra(ScannerActivity.ResultKey) : null;
            ScanTcs?.TrySetResult(text);
            ScanTcs = null;
        }
    }
}
