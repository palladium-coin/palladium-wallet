using NBitcoin;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;
using PalladiumWallet.Tests.Crypto;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// A multisig wallet is created from the CLI (ADR 0056), but the GUI opens the same file: it must show the
/// wallet, sync it and prepare a spend as a PSBT for the cosigners, without ever offering a broadcast it cannot make.
/// </summary>
[Collection("AppPaths")]
public sealed class MultisigGuiTests : VmTestBase
{
    // Regtest, not mainnet: a wallet opened from a file gets the network's real profile, and the real mainnet one
    // anchors headers to checkpoints the fake server does not serve. Regtest has none.
    private (MultisigAccount Account, MainWindowViewModel Vm) OpenMultisigWallet(string? ownWords)
    {
        MainWindowViewModel.ProfileOverride = ChainProfiles.Regtest with { BootstrapServers = [], Checkpoints = [] };
        var (doc, account) = WalletLoader.NewMultisig(2, MultisigAccountTests.AllKeys, ownWords, null, ChainProfiles.Regtest);
        var path = AppPaths.DefaultWalletPath(NetKind.Regtest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WalletStore.Save(doc, path, null);

        WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        _server.Handle("server.features", _ => new { genesis_hash = ChainProfiles.Regtest.GenesisHash });
        var vm = new MainWindowViewModel();
        vm.WizardStartOpenCommand.Execute(null);
        vm.OpenExistingCommand.Execute(null);
        return (account, vm);
    }

    [Fact]
    public Task The_gui_opens_syncs_and_shows_a_multisig_wallet() => UiSession.Run(async () =>
    {
        var (account, vm) = OpenMultisigWallet(MultisigAccountTests.Words1);

        Assert.True(vm.IsWalletOpen);
        await Eventually(() => !vm.IsSyncing && vm.IsConnected && vm.Coins.Count == 1,
            describe: () => $"syncing={vm.IsSyncing} coins={vm.Coins.Count} status='{vm.StatusMessage}'");

        Assert.Contains("0.5", vm.BalanceText);
        Assert.Equal(account.GetReceiveAddress(1).ToString(), vm.ReceiveAddress);   // address 0 is used: the next one
        Assert.NotEmpty(vm.Addresses);
        Assert.Equal("", vm.WalletInfoXpub);          // no single account key stands for a multisig wallet
    });

    [Fact]
    public Task A_spend_from_a_multisig_wallet_is_a_psbt_for_the_cosigners_never_a_broadcast() => UiSession.Run(async () =>
    {
        var (account, vm) = OpenMultisigWallet(MultisigAccountTests.Words1);
        await Eventually(() => !vm.IsSyncing && vm.IsConnected && vm.Coins.Count(c => c.IsSelectable) == 1,
            describe: () => $"syncing={vm.IsSyncing} coins={vm.Coins.Count} status='{vm.StatusMessage}'");

        vm.SendTo = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, PalladiumNetworks.Regtest).ToString();
        vm.SendAmount = "0.1";
        vm.SendFeeRate = "1";
        await vm.PrepareSendCommand.ExecuteAsync(null);

        Assert.False(vm.HasPendingSend, vm.SendPreview);   // nothing to broadcast: one signature of two
        Assert.NotEqual("", vm.PendingPsbtBase64);          // the PSBT to pass on
        var psbt = PSBT.Parse(vm.PendingPsbtBase64, PalladiumNetworks.Regtest);
        Assert.Equal(1, psbt.Inputs[0].PartialSigs.Count);   // this wallet's own signature is already in
        await vm.ConfirmSendCommand.ExecuteAsync(null);
        Assert.Equal(0, _server.CallCount("blockchain.transaction.broadcast"));
    });

    [Fact]
    public Task A_multisig_wallet_without_a_key_prepares_an_unsigned_psbt_and_never_broadcasts() => UiSession.Run(async () =>
    {
        var (_, vm) = OpenMultisigWallet(ownWords: null);
        await Eventually(() => !vm.IsSyncing && vm.IsConnected && vm.Coins.Count(c => c.IsSelectable) == 1,
            describe: () => $"syncing={vm.IsSyncing} coins={vm.Coins.Count} status='{vm.StatusMessage}'");

        vm.SendTo = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, PalladiumNetworks.Regtest).ToString();
        vm.SendAmount = "0.1";
        vm.SendFeeRate = "1";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        await vm.ConfirmSendCommand.ExecuteAsync(null);

        Assert.False(vm.HasPendingSend);
        Assert.Equal(0, PSBT.Parse(vm.PendingPsbtBase64, PalladiumNetworks.Regtest).Inputs[0].PartialSigs.Count);
        Assert.Equal(0, _server.CallCount("blockchain.transaction.broadcast"));
    });
}
