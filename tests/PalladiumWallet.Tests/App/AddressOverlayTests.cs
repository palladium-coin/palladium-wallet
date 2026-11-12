using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;

namespace PalladiumWallet.Tests.App;

/// <summary>The address-detail overlay labels its buttons for the private key, not for the seed.</summary>
[Collection("AppPaths")]
public sealed class AddressOverlayTests : VmTestBase
{
    private static IEnumerable<string> VisibleButtonLabels(Control root) =>
        root.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Content is string)
            .Select(b => (string)b.Content!);

    [Fact]
    public Task Revealing_a_private_key_never_says_seed() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 10_000_000);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, "pw12345678", "keys");
        await Eventually(() => !vm.IsSyncing && vm.Addresses.Count > 0);
        var view = new PalladiumWallet.App.Views.MainView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        vm.ShowAddressInfo(vm.Addresses[0]);
        Dispatcher.UIThread.RunJobs();
        var seed = new[] { Loc.Tr("walletinfo.seed.reveal"), Loc.Tr("walletinfo.seed.hide") };
        var labels = VisibleButtonLabels(view).ToList();
        Assert.Contains(Loc.Tr("addr.privkey.reveal"), labels);
        Assert.DoesNotContain(labels, l => seed.Contains(l));

        vm.RequestPrivKeyRevealCommand.Execute(null); // encrypted wallet: the password prompt opens
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsPrivKeyPromptOpen);
        labels = VisibleButtonLabels(view).ToList();
        Assert.Contains(Loc.Tr("addr.privkey.reveal"), labels);
        Assert.DoesNotContain(labels, l => seed.Contains(l));

        vm.PrivKeyPromptPassword = "pw12345678";
        vm.ConfirmPrivKeyPasswordCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsAddressPrivKeyRevealed);
        labels = VisibleButtonLabels(view).ToList();
        Assert.Contains(Loc.Tr("addr.privkey.hide"), labels);
        Assert.DoesNotContain(labels, l => seed.Contains(l));
        window.Close();
    });

    [Fact]
    public void The_new_labels_exist_in_every_language()
    {
        foreach (var lang in new[] { "it", "en", "es", "fr", "pt", "de", "zh" })
        {
            Loc.SwitchTo(lang);
            foreach (var key in new[] { "addr.privkey.reveal", "addr.privkey.hide" })
                Assert.NotEqual(key, Loc.Tr(key));
        }
    }
}
