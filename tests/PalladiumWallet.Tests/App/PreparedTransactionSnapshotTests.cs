using NBitcoin;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Spv;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// The prepared transaction is a snapshot (ADR 0048): the review shows what it really pays, anything
/// that went into it discards it when it changes, and a sync that takes its coins away drops it.
/// </summary>
[Collection("AppPaths")]
public sealed class PreparedTransactionSnapshotTests : VmTestBase
{
    private Transaction _funding = null!;

    private async Task<(MainWindowViewModel Vm, HdAccount Account)> Open(params long[] coins)
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        (_funding, _) = WireFundedChain(account.GetReceiveAddress(0), coins);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "snapshot");
        await Eventually(() => !vm.IsSyncing && vm.Coins.Count(c => c.IsSelectable) == coins.Length,
            describe: () => $"syncing={vm.IsSyncing} coins={vm.Coins.Count} status='{vm.StatusMessage}'");
        return (vm, account);
    }

    /// <summary>Fills the form with known values and prepares: the starting point of every test.</summary>
    private static async Task Prepare(MainWindowViewModel vm, HdAccount account)
    {
        vm.SendAll = false;
        vm.SendTo = account.GetReceiveAddress(7).ToString();
        vm.SendAmount = "0.05";
        vm.SendFeeRate = "1";
        vm.ClearCoinSelectionCommand.Execute(null);
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingSend, vm.SendPreview);
    }

    [Fact]
    public Task Every_input_of_the_prepared_send_discards_it_when_it_changes() => UiSession.Run(async () =>
    {
        var (vm, account) = await Open(50_000_000, 10_000_000);
        var edits = new (string Name, Action Edit)[]
        {
            ("destination", () => vm.SendTo = account.GetReceiveAddress(8).ToString()),
            ("amount", () => vm.SendAmount = "0.2"),
            ("send all", () => vm.SendAll = true),
            ("fee rate", () => vm.SendFeeRate = "3"),
            ("coin selection", () => vm.ToggleCoinSelectedCommand.Execute(vm.Coins.First(c => c.IsSelectable))),
        };

        foreach (var (name, edit) in edits)
        {
            await Prepare(vm, account);

            edit();

            Assert.False(vm.HasPendingSend, $"{name} did not discard the prepared send");
            Assert.Equal("", vm.SendPreview);
        }
    });

    [Fact]
    public Task The_review_shows_the_full_address_the_amount_and_the_change() => UiSession.Run(async () =>
    {
        var (vm, account) = await Open(50_000_000);

        await Prepare(vm, account);

        Assert.Contains(account.GetReceiveAddress(7).ToString(), vm.SendPreview); // the whole address, not a prefix
        Assert.Contains("0.05", vm.SendPreview);
        Assert.Contains(Loc.Tr("send.review.change").Split('{')[0], vm.SendPreview);
    });

    [Fact]
    public Task A_sync_that_takes_the_coin_away_drops_the_prepared_send() => UiSession.Run(async () =>
    {
        var (vm, account) = await Open(50_000_000);
        await Prepare(vm, account);

        // The coin falls back to the mempool (e.g. a reorganisation): it is no longer spendable.
        var txid = _funding.GetHash().ToString();
        var scripthash = Scripthash.FromAddress(account.GetReceiveAddress(0));
        _server.Handle("blockchain.scripthash.get_history", p =>
            p[0].GetString() == scripthash ? new[] { new { tx_hash = txid, height = 0 } } : Array.Empty<object>());
        await vm.ConnectAndSyncCommand.ExecuteAsync(null);
        await Eventually(() => !vm.IsSyncing && vm.Coins.All(c => !c.IsSelectable));

        Assert.False(vm.HasPendingSend);
        Assert.Equal(Loc.Tr("msg.pending.stale"), vm.SendPreview);
    });

    [Fact]
    public Task A_sync_that_leaves_the_coins_alone_keeps_the_prepared_send() => UiSession.Run(async () =>
    {
        var (vm, account) = await Open(50_000_000);
        await Prepare(vm, account);

        await vm.ConnectAndSyncCommand.ExecuteAsync(null);
        await Eventually(() => !vm.IsSyncing);

        Assert.True(vm.HasPendingSend, vm.SendPreview);
    });

    [Fact]
    public Task Freezing_a_coin_of_the_prepared_send_drops_it() => UiSession.Run(async () =>
    {
        var (vm, account) = await Open(50_000_000);
        await Prepare(vm, account);

        await vm.ToggleFreezeCoinCommand.ExecuteAsync(vm.Coins.Single());

        Assert.False(vm.HasPendingSend);
        Assert.Equal(Loc.Tr("msg.pending.stale"), vm.SendPreview);
    });

    [Fact]
    public Task The_prepared_donation_follows_the_same_rule() => UiSession.Run(async () =>
    {
        var (vm, _) = await Open(50_000_000);
        vm.DonateAmount = "0.01";
        await vm.PrepareDonateCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingDonate, vm.DonatePreview);
        Assert.Contains(MainWindowViewModel.DevAddress, vm.DonatePreview);

        vm.DonateAmount = "0.02";

        Assert.False(vm.HasPendingDonate);
        Assert.Equal("", vm.DonatePreview);
    });
}
