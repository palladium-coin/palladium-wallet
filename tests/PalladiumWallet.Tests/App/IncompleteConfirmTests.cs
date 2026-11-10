using System.Reflection;
using NBitcoin;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// The Confirm commands of Send and Donate broadcast only a fully signed transaction, whatever the state of the
/// button: a watch-only wallet holds an unsigned one, and the user is told so instead of nothing happening.
/// </summary>
[Collection("AppPaths")]
public sealed class IncompleteConfirmTests : VmTestBase
{
    /// <summary>An unsigned transaction, built by a watch-only copy (xpub only) of the wallet the view model holds.</summary>
    private static BuiltTransaction UnsignedTransaction(HdAccount signer, string destination)
    {
        var watchOnly = HdAccount.FromAccountXpub(signer.AccountXpub, signer.Kind, signer.Profile, signer.AccountPath, signer.MasterFingerprint);
        var funding = PalladiumNetworks.Mainnet.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(50_000_000), watchOnly.GetReceiveAddress(0));
        var utxo = new CachedUtxo
        {
            Txid = funding.GetHash().ToString(), Vout = 0, ValueSats = 50_000_000,
            Address = watchOnly.GetReceiveAddress(0).ToString(), Height = 100, Verified = true,
        };
        var built = new TransactionFactory(watchOnly).Build([utxo], new Dictionary<string, Transaction> { [utxo.Txid] = funding },
            BitcoinAddress.Create(destination, PalladiumNetworks.Mainnet), 1_000_000, 1m, changeIndex: 0, tipHeight: 1_000);
        Assert.False(built.Signed);
        return built;
    }

    private static void Inject(MainWindowViewModel vm, string field, BuiltTransaction built) =>
        typeof(MainWindowViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, built);

    private async Task<(MainWindowViewModel Vm, HdAccount Account)> OpenConnected()
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "incomplete");
        await Eventually(() => !vm.IsSyncing && vm.IsConnected && vm.Coins.Count == 1,
            describe: () => $"syncing={vm.IsSyncing} connected={vm.IsConnected} status='{vm.StatusMessage}'");
        return (vm, account);
    }

    [Fact]
    public Task Confirming_an_unsigned_send_broadcasts_nothing_and_says_why() => UiSession.Run(async () =>
    {
        var (vm, account) = await OpenConnected();
        Inject(vm, "_pendingSend", UnsignedTransaction(account, account.GetReceiveAddress(7).ToString()));

        await vm.ConfirmSendCommand.ExecuteAsync(null);

        Assert.Equal(0, _server.CallCount("blockchain.transaction.broadcast"));
        Assert.Equal(Loc.Tr("msg.confirm.incomplete"), vm.SendPreview);
    });

    [Fact]
    public Task Confirming_an_unsigned_donation_broadcasts_nothing_and_says_why() => UiSession.Run(async () =>
    {
        var (vm, account) = await OpenConnected();
        Inject(vm, "_pendingDonate", UnsignedTransaction(account, MainWindowViewModel.DevAddress));

        await vm.ConfirmDonateCommand.ExecuteAsync(null);

        Assert.Equal(0, _server.CallCount("blockchain.transaction.broadcast"));
        Assert.Equal(Loc.Tr("msg.confirm.incomplete"), vm.DonatePreview);
    });
}
