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

    // Avalonia's Dispatch(Func<Task>) returns as soon as the body reaches its first real await:
    // everything after it (including the assertions) would silently never run and the test would
    // pass vacuously. So the async body is driven to completion here, pumping the dispatcher on
    // the UI thread (the continuations of its awaits are posted there), with a hard time limit.
    public static Task Run(Func<Task> body) => Session.Value.Dispatch(() =>
    {
        var task = body();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Async UI test body did not complete within 2 minutes.");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }, CancellationToken.None);

    public static Task Run(Action body) =>
        Session.Value.Dispatch(() => { body(); return Task.CompletedTask; }, CancellationToken.None);
}
