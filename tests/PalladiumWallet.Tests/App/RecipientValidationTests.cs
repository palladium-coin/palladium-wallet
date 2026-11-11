using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;

namespace PalladiumWallet.Tests.App;

/// <summary>A bad recipient is reported in words, never as the parser's exception text ("Invalid string").</summary>
[Collection("AppPaths")]
public sealed class RecipientValidationTests : VmTestBase
{
    private async Task<MainWindowViewModel> Open()
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 10_000_000);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "recipient");
        await Eventually(() => !vm.IsSyncing && vm.Coins.Any(c => c.IsSelectable));
        return vm;
    }

    private static string TestnetAddress() =>
        HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Testnet)
            .GetReceiveAddress(0).ToString();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("TESTNET")] // replaced by a testnet address below: the wrong network
    public Task Send_refuses_an_empty_malformed_or_wrong_network_recipient_in_words(string recipient) => UiSession.Run(async () =>
    {
        var vm = await Open();
        vm.SendTo = recipient == "TESTNET" ? TestnetAddress() : recipient;
        vm.SendAmount = "0.01";
        vm.SendFeeRate = "1";

        await vm.PrepareSendCommand.ExecuteAsync(null);

        Assert.Equal(Loc.Tr("msg.address.invalid"), vm.SendPreview);
        Assert.False(vm.HasPendingSend);
    });

    [Fact]
    public Task Donate_refuses_an_address_of_another_network_in_words() => UiSession.Run(async () =>
    {
        var vm = await Open();
        var saved = MainWindowViewModel.DonationAddressOverride;
        MainWindowViewModel.DonationAddressOverride = TestnetAddress();
        try
        {
            vm.DonateAmount = "0.01";
            vm.SendFeeRate = "1";
            await vm.PrepareDonateCommand.ExecuteAsync(null);
            Assert.Equal(Loc.Tr("msg.address.invalid"), vm.DonatePreview);
            Assert.False(vm.HasPendingDonate);
        }
        finally { MainWindowViewModel.DonationAddressOverride = saved; }
    });

    [Fact]
    public Task A_valid_recipient_still_prepares() => UiSession.Run(async () =>
    {
        var vm = await Open();
        vm.SendTo = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet)
            .GetReceiveAddress(5).ToString();
        vm.SendAmount = "0.01";
        vm.SendFeeRate = "1";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingSend, vm.SendPreview);
    });
}
