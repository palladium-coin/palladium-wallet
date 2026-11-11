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

    // ----- amount validation, through the ViewModel (the Core check is tested in TransactionFactoryLimitsTests) -----

    [Theory]
    [InlineData("0")]
    [InlineData("0.00000000")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public Task A_zero_or_invalid_amount_is_refused_before_anything_is_built(string amount) => UiSession.Run(async () =>
    {
        var (vm, account) = await Open(50_000_000);
        vm.SendAll = false;
        vm.SendTo = account.GetReceiveAddress(7).ToString();
        vm.SendAmount = amount;
        vm.SendFeeRate = "1";

        await vm.PrepareSendCommand.ExecuteAsync(null);

        Assert.False(vm.HasPendingSend);
        Assert.Equal(Loc.Tr("msg.amount.invalid"), vm.SendPreview);
    });

    [Theory]
    [InlineData("0")]
    [InlineData("-0.5")]
    [InlineData("x")]
    public Task A_zero_or_invalid_donation_is_refused_before_anything_is_built(string amount) => UiSession.Run(async () =>
    {
        var (vm, _) = await Open(50_000_000);
        vm.DonateAmount = amount;

        await vm.PrepareDonateCommand.ExecuteAsync(null);

        Assert.False(vm.HasPendingDonate);
        Assert.Equal(Loc.Tr("msg.amount.invalid"), vm.DonatePreview);
    });

    [Fact]
    public Task Send_all_needs_no_amount() => UiSession.Run(async () =>
    {
        var (vm, account) = await Open(50_000_000);
        vm.SendAll = true;
        vm.SendTo = account.GetReceiveAddress(7).ToString();
        vm.SendAmount = "0";                                      // ignored: the amount is "everything minus the fee"
        vm.SendFeeRate = "1";

        await vm.PrepareSendCommand.ExecuteAsync(null);

        Assert.True(vm.HasPendingSend, vm.SendPreview);
    });

    // ----- a Prepare that fails for a reason other than a field edit leaves nothing confirmable (ADR 0048) -----

    /// <summary>
    /// The relay fee arrives from the server after the user typed a fee: no field changed, so no edit hook
    /// discarded the prepared transaction, and the next Prepare is rejected for a reason unrelated to any edit.
    /// </summary>
    private async Task RelayFeeRisesTo(MainWindowViewModel vm, decimal satPerVByte)
    {
        _server.Handle("blockchain.relayfee", _ => satPerVByte / 100_000m);   // PLM/kB
        _server.Handle("blockchain.estimatefee", _ => satPerVByte / 100_000m);
        _server.DropClients();                                    // estimates are fetched when a connection is made
        await Eventually(() => !vm.IsConnected, describe: () => "waiting for the drop to be noticed");
        await vm.ConnectAndSyncCommand.ExecuteAsync(null);
        await Eventually(() => !vm.IsSyncing && vm.IsConnected && vm.HasFeeEstimates,
            describe: () => $"syncing={vm.IsSyncing} estimates={vm.HasFeeEstimates}");
    }

    [Fact]
    public Task A_send_that_fails_to_prepare_leaves_no_earlier_transaction_confirmable() => UiSession.Run(async () =>
    {
        var (vm, account) = await Open(50_000_000);
        await Prepare(vm, account);
        // A rate the user typed (different from the default, so it counts as typed): estimates never overwrite it.
        vm.SendFeeRate = "2";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingSend, vm.SendPreview);           // prepared at 2 sat/vB, confirmable

        await RelayFeeRisesTo(vm, 5);                             // nothing the user typed changed
        Assert.True(vm.HasPendingSend, "a relay-fee update alone must not touch the snapshot; preview=" + vm.SendPreview);

        await vm.PrepareSendCommand.ExecuteAsync(null);           // same fields, now below the relay fee

        Assert.False(vm.HasPendingSend);
        Assert.Equal(string.Format(Loc.Tr("msg.feerate.belowrelay"), "5"), vm.SendPreview);
        var calls = _server.CallCount("blockchain.transaction.broadcast");
        await vm.ConfirmSendCommand.ExecuteAsync(null);           // and confirming sends nothing
        Assert.Equal(calls, _server.CallCount("blockchain.transaction.broadcast"));
    });

    [Fact]
    public Task A_donation_that_fails_to_prepare_leaves_no_earlier_transaction_confirmable() => UiSession.Run(async () =>
    {
        var (vm, _) = await Open(50_000_000);
        vm.SendFeeRate = "2";                                     // typed by the user: estimates never overwrite it
        vm.DonateAmount = "0.01";
        await vm.PrepareDonateCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingDonate, vm.DonatePreview);

        await RelayFeeRisesTo(vm, 5);
        Assert.True(vm.HasPendingDonate, "a relay-fee update alone must not touch the snapshot");
        await vm.PrepareDonateCommand.ExecuteAsync(null);

        Assert.False(vm.HasPendingDonate);
        Assert.Equal(string.Format(Loc.Tr("msg.feerate.belowrelay"), "5"), vm.DonatePreview);
        var calls = _server.CallCount("blockchain.transaction.broadcast");
        await vm.ConfirmDonateCommand.ExecuteAsync(null);
        Assert.Equal(calls, _server.CallCount("blockchain.transaction.broadcast"));
    });
}
