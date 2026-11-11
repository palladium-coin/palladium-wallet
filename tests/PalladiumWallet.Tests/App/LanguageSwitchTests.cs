using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// Texts that are assembled from translated parts (send review, fee hint, connection state) follow the
/// language and the unit when the user changes them, instead of keeping the text of the moment they were made.
/// </summary>
[Collection("AppPaths")]
public sealed class LanguageSwitchTests : VmTestBase
{
    private async Task<MainWindowViewModel> Open()
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        _server.Handle("blockchain.relayfee", _ => 0.00001m);
        _server.Handle("blockchain.estimatefee", p => p[0].GetInt32() switch
        {
            FeePolicy.FastTarget => 0.00005m,
            FeePolicy.NormalTarget => 0.00003m,
            _ => 0.00001m,
        });
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "lang");
        await Eventually(() => vm.HasFeeEstimates && vm.IsConnected && !vm.IsSyncing && vm.Coins.Any(c => c.IsSelectable));
        return vm;
    }

    private static async Task Prepare(MainWindowViewModel vm, HdAccount account)
    {
        vm.SendTo = account.GetReceiveAddress(5).ToString();
        vm.SendAmount = "0.1";
        vm.SendFeeRate = "2";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingSend, vm.SendPreview);
    }

    private static HdAccount Account() =>
        HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);

    [Fact]
    public Task The_send_review_is_rendered_again_in_the_new_language() => UiSession.Run(async () =>
    {
        var vm = await Open();
        await Prepare(vm, Account());
        Assert.Contains("fee ", vm.SendPreview);
        Assert.Contains(string.Format(Loc.Tr("send.review.change"), "").Trim(), vm.SendPreview);

        vm.SetLanguageCommand.Execute("it");

        Assert.Contains("commissione ", vm.SendPreview);
        Assert.DoesNotContain("fee ", vm.SendPreview);
        Assert.Contains(string.Format(Loc.Tr("send.review.change"), "").Trim(), vm.SendPreview); // "Resto:"
        Assert.True(vm.HasPendingSend); // still the same prepared transaction
    });

    [Fact]
    public Task The_send_review_follows_a_change_of_unit() => UiSession.Run(async () =>
    {
        var vm = await Open();
        await Prepare(vm, Account());
        var before = vm.SendPreview;

        vm.SetUnitCommand.Execute("sat");

        Assert.NotEqual(before, vm.SendPreview);
        Assert.Contains("sat", vm.SendPreview);
    });

    [Fact]
    public Task A_discarded_review_is_not_brought_back_by_a_language_change() => UiSession.Run(async () =>
    {
        var vm = await Open();
        await Prepare(vm, Account());
        vm.SendAmount = "0.2"; // anything that went into it changes: the review is dropped
        var dropped = vm.SendPreview;
        Assert.False(vm.HasPendingSend);

        vm.SetLanguageCommand.Execute("de");

        Assert.Equal(dropped, vm.SendPreview);
        Assert.False(vm.HasPendingSend);
    });

    [Fact]
    public Task The_fee_hint_is_rendered_again_in_the_new_language() => UiSession.Run(async () =>
    {
        var vm = await Open();
        Assert.Contains("Network estimate", vm.FeeEstimateHint);

        vm.SetLanguageCommand.Execute("de");

        Assert.Contains("Netzwerkschätzung", vm.FeeEstimateHint);
        Assert.DoesNotContain("Network estimate", vm.FeeEstimateHint);
    });

    [Fact]
    public Task The_connection_state_is_rendered_again_in_the_new_language() => UiSession.Run(async () =>
    {
        var vm = await Open();
        Assert.Equal("connected", vm.ConnectionStatusShort);
        Assert.StartsWith("connected ", vm.ConnectionStatus);
        var endpoint = vm.ConnectionStatus["connected ".Length..];

        vm.SetLanguageCommand.Execute("it");

        Assert.Equal("connesso", vm.ConnectionStatusShort);
        Assert.Equal("connesso " + endpoint, vm.ConnectionStatus);
    });

    [Fact]
    public Task The_donate_review_is_rendered_again_in_the_new_language() => UiSession.Run(async () =>
    {
        var vm = await Open();
        var saved = MainWindowViewModel.DonationAddressOverride;
        MainWindowViewModel.DonationAddressOverride = Account().GetReceiveAddress(9).ToString();
        try
        {
            vm.DonateAmount = "0.1";
            vm.SendFeeRate = "2";
            await vm.PrepareDonateCommand.ExecuteAsync(null);
            Assert.True(vm.HasPendingDonate, vm.DonatePreview);
            Assert.Contains("fee ", vm.DonatePreview);

            vm.SetLanguageCommand.Execute("fr");

            Assert.Contains("frais ", vm.DonatePreview);
            Assert.True(vm.HasPendingDonate);
        }
        finally { MainWindowViewModel.DonationAddressOverride = saved; }
    });
}
