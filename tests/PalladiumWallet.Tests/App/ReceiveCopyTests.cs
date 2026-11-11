using Avalonia.Threading;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;

namespace PalladiumWallet.Tests.App;

/// <summary>The Receive tab's Copy button confirms in place for a moment, then goes back to "Copy".</summary>
[Collection("AppPaths")]
public sealed class ReceiveCopyTests : VmTestBase
{
    [Fact]
    public Task Copying_the_address_flags_the_button_then_clears_it() => UiSession.Run(async () =>
    {
        var saved = MainWindowViewModel.ReceiveCopiedDuration;
        MainWindowViewModel.ReceiveCopiedDuration = TimeSpan.FromMilliseconds(60);
        try
        {
            var vm = new MainWindowViewModel();
            Assert.False(vm.ReceiveCopied);

            vm.NotifyAddressCopied();
            Assert.True(vm.ReceiveCopied);
            Assert.Equal(Loc.Tr("addr.copied"), vm.StatusMessage);

            await Eventually(() => !vm.ReceiveCopied);
        }
        finally { MainWindowViewModel.ReceiveCopiedDuration = saved; }
    });

    [Fact]
    public Task A_second_copy_restarts_the_confirmation_and_a_new_address_clears_it() => UiSession.Run(async () =>
    {
        var saved = MainWindowViewModel.ReceiveCopiedDuration;
        MainWindowViewModel.ReceiveCopiedDuration = TimeSpan.FromMilliseconds(400);
        try
        {
            var vm = new MainWindowViewModel();
            vm.NotifyAddressCopied();
            await Task.Delay(250);
            Dispatcher.UIThread.RunJobs();
            vm.NotifyAddressCopied();                 // restarts the window: the first timer must not clear it
            await Task.Delay(250);                    // 500 ms after the first copy, 250 after the second
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.ReceiveCopied);

            vm.ReceiveAddress = "plm1qexampleaddressfortest";   // a different address has not been copied yet
            Assert.False(vm.ReceiveCopied);
        }
        finally { MainWindowViewModel.ReceiveCopiedDuration = saved; }
    });

    [Fact]
    public Task The_receive_address_shows_its_derivation_path() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        Assert.Equal("", vm.ReceivePath);                 // no wallet open yet
        RestoreFlow(vm, Mnemonic, null, "path");
        Assert.Equal("m/84'/746'/0'/0/0", vm.ReceivePath); // native SegWit, coin type 746, first receive index
        Assert.False(string.IsNullOrEmpty(vm.ReceiveAddress));
    });

    [Fact]
    public void Every_language_has_the_copied_label()
    {
        foreach (var lang in new[] { "it", "en", "es", "fr", "pt", "de", "zh" })
        {
            Loc.SwitchTo(lang);
            Assert.False(string.IsNullOrWhiteSpace(Loc.Tr("receive.copied")));
            Assert.NotEqual("receive.copied", Loc.Tr("receive.copied"));
        }
        Loc.SwitchTo("en");
    }
}
