using Avalonia.Threading;
using NBitcoin;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Tests.Net;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// What the ViewModel does with a live connection: it closes the witness connections of a connection it
/// replaces, and it reacts to the server's notifications (a payment to an address the wallet already used).
/// </summary>
[Collection("AppPaths")]
public sealed class LiveConnectionTests : VmTestBase, IDisposable
{
    private async Task<MainWindowViewModel> OpenFundedWallet(HdAccount account, string name)
    {
        WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, name);
        await Eventually(() => !vm.IsSyncing && vm.History.Count == 1 && vm.History[0].Verified,
            describe: () => $"syncing={vm.IsSyncing} rows={vm.History.Count} status='{vm.StatusMessage}'");
        return vm;
    }

    [Fact]
    public Task The_witness_of_a_dead_connection_is_closed_when_the_primary_is_replaced() => UiSession.Run(async () =>
    {
        // The wallet's server is _server (the primary); the only bootstrap server is a second fake, which is
        // therefore the witness (the connector never uses the primary as a witness).
        await using var witness = new FakeElectrumServer();
        witness.Handle("blockchain.headers.subscribe", _ => new { height = 200, hex = new string('0', 160) });
        MainWindowViewModel.ProfileOverride = ChainProfiles.Mainnet with
        {
            BootstrapServers = [new ServerEndpoint(witness.Host, witness.Port, witness.Port)],
            Checkpoints = [],
        };
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        var vm = new MainWindowViewModel { ServerHost = _server.Host, ServerPort = _server.Port.ToString() };
        RestoreFlow(vm, Mnemonic, null, "witness");
        await Eventually(() => !vm.IsSyncing && vm.History.Count == 1 && vm.History[0].Verified && witness.ConnectedCount == 1,
            describe: () => $"syncing={vm.IsSyncing} rows={vm.History.Count} witness accepted={witness.AcceptedCount} open={witness.ConnectedCount}");
        Assert.Equal(0, witness.CallCount("blockchain.scripthash.get_history")); // it is the witness, not the primary

        // The primary connection dies; syncing again replaces it. The previous witness must go with it.
        _server.DropClients();
        await Eventually(() => !vm.IsConnected, describe: () => "waiting for the drop to be noticed");
        await vm.ConnectAndSyncCommand.ExecuteAsync(null);
        // (The wallet may also reconnect by itself, so the number of reconnections is not fixed: what matters
        // is that every dead connection's witness is closed, i.e. one witness stays open, never one per reconnection.)
        await Eventually(() => !vm.IsSyncing && vm.IsConnected && witness.AcceptedCount >= 2,
            describe: () => $"syncing={vm.IsSyncing} connected={vm.IsConnected} witness accepted={witness.AcceptedCount}");

        await Eventually(() => witness.ConnectedCount == 1,
            describe: () => $"a witness connection was leaked: accepted={witness.AcceptedCount} open={witness.ConnectedCount}");
        await Task.Delay(300);                                   // nothing is still closing or opening late
        Assert.Equal(1, witness.ConnectedCount);
    });

    [Fact]
    public Task A_payment_to_an_address_the_wallet_already_used_is_noticed_from_the_servers_notification() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        var used = account.GetReceiveAddress(0);
        var usedHash = Scripthash.FromAddress(used);
        var subscribed = new System.Collections.Concurrent.ConcurrentBag<string>();
        // Record every scripthash the wallet subscribes to (wired before the wallet opens).
        WireFundedChain(used, 50_000_000);
        _server.Handle("blockchain.scripthash.subscribe", p => { subscribed.Add(p[0].GetString()!); return null; });
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "notify");
        await Eventually(() => !vm.IsSyncing && vm.History.Count == 1 && vm.History[0].Verified,
            describe: () => $"syncing={vm.IsSyncing} rows={vm.History.Count} status='{vm.StatusMessage}'");

        // Fix 14: on a new connection the wallet already knows the used address (index 0 is below the known index),
        // and a subscription is per connection: it must be subscribed again, not only the unused ones past it.
        subscribed.Clear();
        _server.DropClients();
        await Eventually(() => !vm.IsConnected, describe: () => "waiting for the drop to be noticed");
        await vm.ConnectAndSyncCommand.ExecuteAsync(null);
        await Eventually(() => !vm.IsSyncing && vm.IsConnected && subscribed.Contains(usedHash),
            describe: () => $"subscribed to {subscribed.Count} scripthashes, the used address is not among them");

        // A second payment arrives at that used address: the server announces it, with no new block.
        var second = PalladiumNetworks.Mainnet.CreateTransaction();
        second.Inputs.Add(new TxIn(new OutPoint(uint256.One, 1)));
        second.Outputs.Add(Money.Satoshis(20_000_000), used);
        var first = vm.History[0].Txid;
        _server.Handle("blockchain.scripthash.get_history", p => p[0].GetString() == usedHash
            ? new object[] { new { tx_hash = first, height = 100 }, new { tx_hash = second.GetHash().ToString(), height = 0 } }
            : Array.Empty<object>());
        _server.Handle("blockchain.transaction.get", p => p[0].GetString() == second.GetHash().ToString()
            ? second.ToHex()
            : FirstTxHex(account));
        await _server.NotifyAsync("blockchain.scripthash.subscribe", new[] { usedHash, "new-status" });

        await Eventually(() => vm.History.Count == 2,
            describe: () => $"rows={vm.History.Count} syncing={vm.IsSyncing} status='{vm.StatusMessage}'");
        Assert.Contains(vm.History, h => h.Txid == second.GetHash().ToString());
        Dispatcher.UIThread.RunJobs();
    });

    // ----- the witness clamp (ADR 0045), through the wallet's real witness wiring -----

    /// <summary>Opens a wallet whose primary says the tip is 200 while the (only) witness says <paramref name="witnessTip"/>.</summary>
    private async Task<MainWindowViewModel> OpenWithWitnessTip(int witnessTip)
    {
        var witness = new FakeElectrumServer();
        _extraServers.Add(witness);
        witness.Handle("blockchain.headers.subscribe", _ => new { height = witnessTip, hex = new string('0', 160) });
        MainWindowViewModel.ProfileOverride = ChainProfiles.Mainnet with
        {
            BootstrapServers = [new ServerEndpoint(witness.Host, witness.Port, witness.Port)],
            Checkpoints = [],
        };
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 50_000_000);   // the coin is at height 100; the primary tip is 200
        var vm = new MainWindowViewModel { ServerHost = _server.Host, ServerPort = _server.Port.ToString() };
        RestoreFlow(vm, Mnemonic, null, "clamp");
        await Eventually(() => !vm.IsSyncing && vm.Coins.Count == 1,
            describe: () => $"syncing={vm.IsSyncing} coins={vm.Coins.Count} status='{vm.StatusMessage}'");
        return vm;
    }

    private readonly List<FakeElectrumServer> _extraServers = [];

    public new void Dispose()
    {
        foreach (var server in _extraServers)
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }

    [Fact]
    public Task A_primary_that_inflates_the_tip_cannot_make_a_fresh_coin_spendable() => UiSession.Run(async () =>
    {
        // The primary claims 200 (the coin would have 101 confirmations); the witness says 100 (1 confirmation).
        var vm = await OpenWithWitnessTip(100);

        Assert.False(vm.Coins.Single().IsSelectable, "the coin counted confirmations from the primary's inflated tip");
        Assert.Equal(Loc.Tr("coins.status.confirming"), vm.Coins.Single().Status);
    });

    [Fact]
    public Task A_witness_that_agrees_with_the_primary_leaves_the_coin_spendable() => UiSession.Run(async () =>
    {
        var vm = await OpenWithWitnessTip(200);

        Assert.True(vm.Coins.Single().IsSelectable);
    });

    [Fact]
    public Task A_witness_a_block_or_two_behind_is_tolerated() => UiSession.Run(async () =>
    {
        var vm = await OpenWithWitnessTip(198);                      // within TipSkewTolerance

        Assert.True(vm.Coins.Single().IsSelectable);
    });

    /// <summary>The funding transaction WireFundedChain registered, rebuilt the same way (same inputs, same output).</summary>
    private static string FirstTxHex(HdAccount account)
    {
        var tx = PalladiumNetworks.Mainnet.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(Money.Satoshis(50_000_000), account.GetReceiveAddress(0));
        return tx.ToHex();
    }
}
