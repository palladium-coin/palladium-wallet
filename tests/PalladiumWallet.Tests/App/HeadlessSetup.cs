using Avalonia;
using Avalonia.Headless;
using PalladiumWallet.Tests.App;

// Boots the real Avalonia Application headlessly (no window, no GPU) so ViewModels that use
// the dispatcher (DispatcherTimer, UIThread.Post) can be tested. The xunit-v3-only helper
// package (Avalonia.Headless.XUnit) is deliberately not used: this suite is on xunit 2.
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace PalladiumWallet.Tests.App;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::PalladiumWallet.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>One shared headless Avalonia session; tests run their body on its UI thread.</summary>
internal static class UiSession
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)));

    public static Task Run(Func<Task> body) => Session.Value.Dispatch(body, CancellationToken.None);

    public static Task Run(Action body) =>
        Session.Value.Dispatch(() => { body(); return Task.CompletedTask; }, CancellationToken.None);
}
