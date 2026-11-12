using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Tests.Net;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// The GUI's view model against a real regtest node and plm-fulcrum, driven by tests/regtest/e2e.sh (which starts
/// both, funds the wallet of <see cref="VmTestBase.Mnemonic"/> with four coins (one per test) and sets the variables). Does nothing
/// (passes) when they are not set, like the live-server tests, so the default run stays offline.
///   PALLADIUM_TEST_REGTEST_SERVER=host:port   the plm-fulcrum tcp endpoint
///   PALLADIUM_TEST_REGTEST_DEST=ADDRESS       a regtest address to pay
/// The Donate tab pays a fixed mainnet address; here <c>DonationAddressOverride</c> points it at <c>DEST</c>.
/// </summary>
[Collection("AppPaths")]
public sealed class RegtestGuiTests : VmTestBase
{
    private static readonly string? Server = Environment.GetEnvironmentVariable("PALLADIUM_TEST_REGTEST_SERVER");
    private static readonly string? Dest = Environment.GetEnvironmentVariable("PALLADIUM_TEST_REGTEST_DEST");
    private static bool Enabled => !string.IsNullOrEmpty(Server) && !string.IsNullOrEmpty(Dest);

    // The tests share one funded wallet: a coin spent by one test (still in the mempool) is never picked by another,
    // even if a first partial snapshot of the next test's sync still lists it. Identical inputs would build the
    // identical transaction, and the node would rightly say it already has it.
    private static readonly HashSet<string> SpentFundingTxids = [];

    private static CoinRow NextFreshCoin(MainWindowViewModel vm)
    {
        lock (SpentFundingTxids)
            return vm.Coins.First(c => c.IsSelectable && !SpentFundingTxids.Contains(c.Txid));
    }

    private static (string Host, int Port) Split(string endpoint) =>
        (endpoint.Split(':')[0], int.Parse(endpoint.Split(':')[1]));

    /// <summary>A view model on regtest, talking to <paramref name="host"/>:<paramref name="port"/>, with its wallet synced.</summary>
    private async Task<MainWindowViewModel> Open(string host, int port, string name)
    {
        MainWindowViewModel.ProfileOverride = ChainProfiles.Regtest with { BootstrapServers = [], Checkpoints = [] };
        new AppConfig { LastServerHost = host, LastServerPort = port, LastServerUseSsl = false }.Save(AppPaths.ConfigPath());
        var vm = new MainWindowViewModel { ServerHost = host, ServerPort = port.ToString() };
        RestoreFlow(vm, Mnemonic, null, name);
        await Eventually(() => !vm.IsSyncing && vm.IsConnected
                && vm.Coins.Any(c => c.IsSelectable && !SpentFundingTxids.Contains(c.Txid)),
            timeoutMs: 60_000,
            describe: () => $"syncing={vm.IsSyncing} connected={vm.IsConnected} coins={vm.Coins.Count} status='{vm.StatusMessage}'");
        return vm;
    }

    private static async Task Prepare(MainWindowViewModel vm)
    {
        vm.ClearCoinSelectionCommand.Execute(null);
        var coin = NextFreshCoin(vm);                                                  // one coin: no ambiguity
        lock (SpentFundingTxids)
            SpentFundingTxids.Add(coin.Txid);
        vm.ToggleCoinSelectedCommand.Execute(coin);
        vm.SendAll = false;
        vm.SendTo = Dest!;
        vm.SendAmount = "0.1";
        vm.SendFeeRate = "2";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingSend, vm.SendPreview);
        // The review card as the real node's transaction fills it: what is paid and to whom on top, the rest below,
        // and no amber notice (a prepared send is not a problem).
        Assert.Contains(Dest!, vm.SendPreviewHeadline);
        Assert.NotEmpty(vm.SendPreviewDetail);
        Assert.False(vm.ShowNotice, vm.StatusMessage);
    }

    [Fact]
    public Task The_coins_tab_freeze_and_a_send_work_against_a_real_node() => UiSession.Run(async () =>
    {
        if (!Enabled) return;
        var (host, port) = Split(Server!);
        var vm = await Open(host, port, "gui-real");

        // Coins tab: the coins are real, and a freeze takes one out of the balance and shows up under it.
        var coin = NextFreshCoin(vm);
        var before = vm.BalanceText;
        await vm.ToggleFreezeCoinCommand.ExecuteAsync(coin);
        Assert.NotEqual(before, vm.BalanceText);
        Assert.NotEqual("", vm.FrozenText);
        await vm.ToggleFreezeCoinCommand.ExecuteAsync(vm.Coins.Single(c => c.Txid == coin.Txid && c.Vout == coin.Vout));
        Assert.Equal(before, vm.BalanceText);
        Assert.Equal("", vm.FrozenText);

        // Send: prepared from one chosen coin, broadcast to the node, which accepts it.
        await Prepare(vm);
        await vm.ConfirmSendCommand.ExecuteAsync(null);
        Assert.StartsWith(Loc.Tr("msg.broadcasted"), vm.SendPreview);
        Assert.False(vm.HasPendingSend);
    });

    [Fact]
    public Task A_reply_with_another_txid_is_reconciled_against_the_real_server() => UiSession.Run(async () =>
    {
        if (!Enabled) return;
        var (host, port) = Split(Server!);
        await using var proxy = new LyingProxy(host, port);
        var vm = await Open(proxy.Host, proxy.Port, "gui-swap");
        await Prepare(vm);

        // The real server takes the transaction, the client is told another txid: the server's own copy decides.
        proxy.Behaviour = LyingProxy.Mode.Swap;
        await vm.ConfirmSendCommand.ExecuteAsync(null);

        Assert.Equal(1, proxy.Broadcasts);
        Assert.StartsWith(Loc.Tr("msg.broadcast.innetwork").Split('{')[0], vm.SendPreview);
        Assert.False(vm.HasPendingSend);
    });

    [Fact]
    public Task A_dropped_broadcast_stays_pending_and_the_same_bytes_can_be_sent_again() => UiSession.Run(async () =>
    {
        if (!Enabled) return;
        var (host, port) = Split(Server!);
        await using var proxy = new LyingProxy(host, port);
        var vm = await Open(proxy.Host, proxy.Port, "gui-drop");
        await Prepare(vm);

        // The server never gets the transaction but the client is told it did (with another txid).
        proxy.Behaviour = LyingProxy.Mode.Drop;
        await vm.ConfirmSendCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Tr("msg.broadcast.notinnetwork"), vm.SendPreview);
        Assert.True(vm.HasPendingSend);

        // The proxy turns honest: the same prepared transaction goes through.
        proxy.Behaviour = LyingProxy.Mode.Honest;
        await vm.ConfirmSendCommand.ExecuteAsync(null);
        Assert.StartsWith(Loc.Tr("msg.broadcasted"), vm.SendPreview);
        Assert.False(vm.HasPendingSend);
    });

    [Fact]
    public Task A_donation_is_broadcast_to_the_real_node() => UiSession.Run(async () =>
    {
        if (!Enabled) return;
        var (host, port) = Split(Server!);
        var vm = await Open(host, port, "gui-donate");
        MainWindowViewModel.DonationAddressOverride = Dest;
        try
        {
            vm.ClearCoinSelectionCommand.Execute(null);
            var coin = NextFreshCoin(vm);
            lock (SpentFundingTxids)
                SpentFundingTxids.Add(coin.Txid);
            vm.ToggleCoinSelectedCommand.Execute(coin);
            vm.DonateAmount = "0.1";
            vm.SendFeeRate = "2";
            await vm.PrepareDonateCommand.ExecuteAsync(null);
            Assert.True(vm.HasPendingDonate, vm.DonatePreview);

            await vm.ConfirmDonateCommand.ExecuteAsync(null);
            Assert.StartsWith(Loc.Tr("msg.donate.thanks"), vm.DonatePreview);
            Assert.False(vm.HasPendingDonate);
        }
        finally { MainWindowViewModel.DonationAddressOverride = null; }
    });
}
