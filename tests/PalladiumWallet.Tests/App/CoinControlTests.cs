using Avalonia.VisualTree;
using NBitcoin;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Core.Storage;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// The Coins tab: freezing a coin takes it out of the balance and out of every spend, the freeze
/// survives a save and a sync, and a coin selection restricts the next send to the chosen coins.
/// </summary>
[Collection("AppPaths")]
public sealed class CoinControlTests : VmTestBase
{
    private const long Big = 50_000_000;   // vout 0: 0.5 PLM
    private const long Small = 10_000_000; // vout 1: 0.1 PLM

    private Transaction _funding = null!;

    private async Task<(MainWindowViewModel Vm, HdAccount Account)> OpenWithTwoCoins()
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        (_funding, _) = WireFundedChain(account.GetReceiveAddress(0), Big, Small);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "coins");
        await Eventually(() => !vm.IsSyncing && vm.Coins.Count(c => c.IsSelectable) == 2,
            describe: () => $"syncing={vm.IsSyncing} coins={vm.Coins.Count} status='{vm.StatusMessage}'");
        return (vm, account);
    }

    [Fact]
    public Task The_coins_tab_renders_one_row_per_coin() => UiSession.Run(async () =>
    {
        var (vm, _) = await OpenWithTwoCoins();
        vm.SelectedTabIndex = 5; // the Coins tab
        var view = new PalladiumWallet.App.Views.MainView { DataContext = vm };
        var window = new Avalonia.Controls.Window { Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var freezeButtons = view.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Count(b => b.Content as string == Loc.Tr("coins.freeze"));
        Assert.Equal(2, freezeButtons);
        window.Close();
    });

    [Fact]
    public Task Freezing_a_coin_removes_it_from_the_balance_and_survives_a_save_and_a_sync() => UiSession.Run(async () =>
    {
        var (vm, _) = await OpenWithTwoCoins();
        Assert.Contains("0.6", vm.BalanceText);

        await vm.ToggleFreezeCoinCommand.ExecuteAsync(vm.Coins.Single(c => c.Vout == 0));

        var frozen = vm.Coins.Single(c => c.Vout == 0);
        Assert.True(frozen.IsFrozen);
        Assert.False(frozen.IsSelectable);
        Assert.Equal(Loc.Tr("coins.unfreeze"), frozen.FreezeText);
        Assert.Contains("0.1", vm.BalanceText);                   // only the 0.1 PLM coin is left
        var stored = WalletStore.Load(Assert.Single(AppPaths.WalletFiles(NetKind.Mainnet)));
        Assert.Contains(stored.Cache!.Utxos, u => u.Vout == 0 && u.Frozen);

        await vm.ConnectAndSyncCommand.ExecuteAsync(null);
        await Eventually(() => !vm.IsSyncing);
        Assert.True(vm.Coins.Single(c => c.Vout == 0).IsFrozen);
        Assert.Contains("0.1", vm.BalanceText);
    });

    [Fact]
    public Task The_frozen_total_is_shown_under_the_balance_and_goes_away_on_unfreeze() => UiSession.Run(async () =>
    {
        var (vm, _) = await OpenWithTwoCoins();
        Assert.Equal("", vm.FrozenText);                          // nothing frozen: no extra line

        await vm.ToggleFreezeCoinCommand.ExecuteAsync(vm.Coins.Single(c => c.Vout == 0)); // the 0.5 PLM coin
        Assert.Contains("0.5", vm.FrozenText);
        Assert.Contains("0.1", vm.BalanceText);                   // and it is not in the balance

        await vm.ToggleFreezeCoinCommand.ExecuteAsync(vm.Coins.Single(c => c.Vout == 0));
        Assert.Equal("", vm.FrozenText);
    });

    [Fact]
    public Task The_frozen_total_survives_a_sync() => UiSession.Run(async () =>
    {
        var (vm, _) = await OpenWithTwoCoins();
        await vm.ToggleFreezeCoinCommand.ExecuteAsync(vm.Coins.Single(c => c.Vout == 0));
        await vm.ConnectAndSyncCommand.ExecuteAsync(null);
        await Eventually(() => !vm.IsSyncing);
        Assert.Contains("0.5", vm.FrozenText);
    });

    [Fact]
    public Task The_coin_selection_summary_is_in_the_desktop_and_the_phone_layout_of_the_send_tab() => UiSession.Run(async () =>
    {
        var (vm, _) = await OpenWithTwoCoins();
        vm.ToggleCoinSelectedCommand.Execute(vm.Coins.Single(c => c.Vout == 1));
        vm.SelectedTabIndex = 1; // Send
        var view = new PalladiumWallet.App.Views.MainView { DataContext = vm };
        var window = new Avalonia.Controls.Window { Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Desktop and phone layout each carry the summary (only one is visible at a time); the Coins tab
        // is not built while another tab is selected.
        var summaries = view.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()
            .Count(t => t.Text == vm.CoinSelectionText);
        Assert.Equal(2, summaries);
        window.Close();
    });

    [Fact]
    public Task A_coin_selection_limits_the_next_send_to_the_selected_coins() => UiSession.Run(async () =>
    {
        var (vm, account) = await OpenWithTwoCoins();
        vm.ToggleCoinSelectedCommand.Execute(vm.Coins.Single(c => c.Vout == 1)); // 0.1 PLM only
        Assert.True(vm.HasCoinSelection);
        Assert.False(string.IsNullOrEmpty(vm.CoinSelectionText));

        vm.SendTo = account.GetReceiveAddress(7).ToString();
        vm.SendAmount = "0.2";                                    // more than the selected coin holds
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.False(vm.HasPendingSend);
        Assert.Contains("Insufficient funds", vm.SendPreview);

        vm.SendAmount = "0.05";                                   // fits in the selected coin
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingSend, vm.SendPreview);

        vm.ClearCoinSelectionCommand.Execute(null);
        Assert.False(vm.HasCoinSelection);
        vm.SendAmount = "0.2";                                    // all coins again: now it fits
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingSend, vm.SendPreview);
    });

    [Fact]
    public Task A_frozen_coin_cannot_be_selected_and_freezing_a_selected_one_drops_it() => UiSession.Run(async () =>
    {
        var (vm, _) = await OpenWithTwoCoins();
        vm.ToggleCoinSelectedCommand.Execute(vm.Coins.Single(c => c.Vout == 1));
        Assert.True(vm.HasCoinSelection);

        await vm.ToggleFreezeCoinCommand.ExecuteAsync(vm.Coins.Single(c => c.Vout == 1));
        Assert.False(vm.HasCoinSelection);

        vm.ToggleCoinSelectedCommand.Execute(vm.Coins.Single(c => c.Vout == 1));
        Assert.False(vm.HasCoinSelection);
        Assert.Equal(Loc.Tr("msg.coins.notspendable"), vm.StatusMessage);
    });

    [Fact]
    public Task A_selected_coin_that_becomes_unconfirmed_blocks_the_send_until_it_is_deselected() => UiSession.Run(async () =>
    {
        var (vm, account) = await OpenWithTwoCoins();
        vm.ToggleCoinSelectedCommand.Execute(vm.Coins.Single(c => c.Vout == 1));

        // The selected coin falls back to the mempool (e.g. a reorganisation) and the wallet resyncs.
        var txid = _funding.GetHash().ToString();
        var scripthash = Scripthash.FromAddress(account.GetReceiveAddress(0));
        _server.Handle("blockchain.scripthash.get_history", p =>
            p[0].GetString() == scripthash ? new[] { new { tx_hash = txid, height = 0 } } : Array.Empty<object>());
        await vm.ConnectAndSyncCommand.ExecuteAsync(null);
        await Eventually(() => !vm.IsSyncing && !vm.Coins.Single(c => c.Vout == 1).IsSelectable);

        vm.SendTo = account.GetReceiveAddress(7).ToString();
        vm.SendAmount = "0.05";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.False(vm.HasPendingSend);
        Assert.Equal(Loc.Tr("msg.coins.notspendable"), vm.SendPreview);

        // Deselecting lifts the block: the send now reaches the wallet's own check, and both coins are
        // still unconfirmed, so that check is the one that answers.
        vm.ToggleCoinSelectedCommand.Execute(vm.Coins.Single(c => c.Vout == 1));
        Assert.False(vm.HasCoinSelection);
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.False(vm.HasPendingSend);
        Assert.Contains("No spendable UTXOs", vm.SendPreview);
    });
}
