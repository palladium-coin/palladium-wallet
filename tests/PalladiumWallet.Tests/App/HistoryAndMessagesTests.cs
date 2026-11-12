using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.App.Views;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.App;

/// <summary>History columns and empty states, the wording of pending amounts, transaction-details fee row, inline messages.</summary>
[Collection("AppPaths")]
public sealed class HistoryAndMessagesTests : VmTestBase
{
    private static readonly string Txid = new string('a', 20) + new string('b', 24) + new string('c', 20);

    private MainWindowViewModel OpenWallet(string name)
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, name);
        return vm;
    }

    [Fact]
    public Task History_rows_show_confirmations_and_a_short_txid() => UiSession.Run(() =>
    {
        var vm = OpenWallet("hist-rows");
        Assert.False(vm.HasHistory);
        vm.ApplyCache(new SyncCache
        {
            TipHeight = 105,
            History =
            [
                new CachedTx { Txid = Txid, Height = 105, DeltaSats = 100_000, Verified = true }, // newest block: 1 confirmation
                new CachedTx { Txid = Txid, Height = 100, DeltaSats = -5_000, Verified = true },
                new CachedTx { Txid = Txid, Height = 0, DeltaSats = 7, Verified = false },
            ],
        });
        Assert.True(vm.HasHistory);
        Assert.Equal(["1", "6", Loc.Tr("tx.mempool")], vm.History.Select(r => r.Conferma));
        Assert.Equal($"{Txid[..10]}…{Txid[^10..]}", vm.History[0].TxidShort);
        Assert.Equal(Txid, vm.History[0].Txid);                    // the full id is still there for the details
        vm.ApplyCache(new SyncCache());
        Assert.False(vm.HasHistory);
    });

    [Fact]
    public Task Empty_history_and_empty_contacts_say_so_and_the_headers_follow_the_rows() => UiSession.Run(() =>
    {
        var vm = OpenWallet("hist-empty");
        var view = new MainView { DataContext = vm };
        var window = new Window { Width = 1020, Height = 720, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        bool Shown(string text) => view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);
        Assert.True(Shown(Loc.Tr("history.empty")));
        Assert.False(Shown(Loc.Tr("history.col.conf")));           // no header over an empty list
        vm.SelectedTabIndex = 4;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Shown(Loc.Tr("contacts.empty.hint")));

        vm.SelectedTabIndex = 0;
        vm.ApplyCache(new SyncCache { TipHeight = 10, History = [new CachedTx { Txid = Txid, Height = 10, DeltaSats = 1 }] });
        Dispatcher.UIThread.RunJobs();
        Assert.False(Shown(Loc.Tr("history.empty")));
        Assert.True(Shown(Loc.Tr("history.col.conf")));
        Assert.True(Shown(Loc.Tr("history.col.amount")));
        window.Close();
    });

    [Fact]
    public Task Pending_money_going_out_is_not_called_not_yet_spendable() => UiSession.Run(() =>
    {
        var vm = OpenWallet("pending");
        vm.ApplyCache(new SyncCache { TipHeight = 10, History = [new CachedTx { Txid = Txid, Height = 0, DeltaSats = -10_000_282 }] });
        Assert.Contains(Loc.Tr("msg.pending.out"), vm.UnconfirmedText);
        Assert.Contains("-", vm.UnconfirmedText);
        Assert.DoesNotContain(Loc.Tr("msg.notspendable"), vm.UnconfirmedText);

        vm.ApplyCache(new SyncCache { TipHeight = 10, History = [new CachedTx { Txid = Txid, Height = 0, DeltaSats = 500 }] });
        Assert.StartsWith($"{Loc.Tr("msg.pending")}: +", vm.UnconfirmedText);
        Assert.Contains(Loc.Tr("msg.notspendable"), vm.UnconfirmedText); // incoming does wait for its confirmations
    });

    private static TransactionDetails Details(long net, long? fee) => new()
    {
        Txid = Txid, Height = 10, Confirmations = 2, NetSats = net, FeeSats = fee, TotalSize = 200, VirtualSize = 141,
        Version = 2, LockTime = 0, RbfSignaled = true, Verified = true, BlockTime = null,
        TotalOutSats = 1000, TotalInSats = 1100,
        Inputs = [new TxInputInfo(Txid, 0, 1100, "plm1qsender", net < 0, false)],
        Outputs = [new TxOutputInfo(0, 1000, "plm1qdest", "P2WPKH", null, net >= 0)],
    };

    [Fact]
    public Task The_fee_row_is_only_for_transactions_the_wallet_paid() => UiSession.Run(() =>
    {
        var received = new TransactionDetailsViewModel(Details(+1000, 100), Loc.SwitchTo("en"), "PLM");
        Assert.False(received.ShowFee);                            // the sender paid it
        var sent = new TransactionDetailsViewModel(Details(-1100, 100), Loc.SwitchTo("en"), "PLM");
        Assert.True(sent.ShowFee);
        Assert.StartsWith("-", sent.FeeText);
    });

    [Fact]
    public Task Confirmations_and_the_notice_only_report_problems() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.StatusMessage = "Invalid words";
        Assert.True(vm.ShowNotice);
        vm.SetLanguageCommand.Execute("en");                        // saves the settings: "Settings saved" is a confirmation
        Assert.Equal(Loc.Tr("msg.settings.saved"), vm.StatusMessage);
        Assert.False(vm.ShowNotice);                                // still in the status bar, not in an amber box
        vm.StatusMessage = "Wrong password";                        // anything assigned after is a problem again
        Assert.True(vm.ShowNotice);
    });

    [Fact]
    public Task The_balance_card_gets_compact_in_a_short_window() => UiSession.Run(() =>
    {
        var vm = OpenWallet("compact");
        var view = new MainView { DataContext = vm };
        var window = new Window { Width = 760, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        double CardHeight() => view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("balance-hero")).Bounds.Height;
        var tall = CardHeight();
        Assert.DoesNotContain("short", view.Classes);

        window.Height = 600;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Contains("short", view.Classes);
        var hero = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("balance-hero"));
        Assert.True(CardHeight() < tall - 15, $"{CardHeight()} vs {tall}; padding {hero.Padding}; view classes {string.Join(",", view.Classes)}");
        window.Close();
    });

    [Fact]
    public void The_new_labels_exist_in_every_language()
    {
        foreach (var lang in new[] { "it", "en", "es", "fr", "pt", "de", "zh" })
        {
            Loc.SwitchTo(lang);
            foreach (var key in new[] { "history.col.conf", "history.col.amount", "history.empty", "contacts.empty.hint", "msg.pending.out" })
                Assert.NotEqual(key, Loc.Tr(key));
        }
        Loc.SwitchTo("en");
    }
}
