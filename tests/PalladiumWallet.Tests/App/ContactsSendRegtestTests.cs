using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Storage;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// Contacts and the Send tab against a real regtest node and plm-fulcrum: save several addresses as contacts, pay each
/// through the Send tab's contact picker, pay the same contact twice, add a contact while the wallet is syncing, and
/// check the contacts survive in the wallet file. Does nothing (passes) unless the variables are set, so the default
/// run stays offline:
///   PALLADIUM_TEST_REGTEST_SERVER=host:port           the plm-fulcrum tcp endpoint
///   PALLADIUM_TEST_REGTEST_CONTACTS=ADDR1,ADDR2,...   regtest addresses of different kinds to pay
/// The wallet of <see cref="VmTestBase.Mnemonic"/> must hold at least seven confirmed coins.
/// </summary>
[Collection("AppPaths")]
public sealed class ContactsSendRegtestTests : VmTestBase
{
    private static readonly string? Server = Environment.GetEnvironmentVariable("PALLADIUM_TEST_REGTEST_SERVER");
    private static readonly string[] Addresses =
        (Environment.GetEnvironmentVariable("PALLADIUM_TEST_REGTEST_CONTACTS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    // The tests share one funded wallet: a coin one test spent (still in the mempool) is never picked by another,
    // or the same inputs would build the same transaction and the node would say it already has it.
    private static readonly HashSet<string> Used = [];
    private static bool Enabled => !string.IsNullOrEmpty(Server) && Addresses.Length >= 3;

    private async Task<MainWindowViewModel> Open(string name)
    {
        var host = Server!.Split(':')[0];
        var port = int.Parse(Server.Split(':')[1]);
        MainWindowViewModel.ProfileOverride = ChainProfiles.Regtest with { BootstrapServers = [], Checkpoints = [] };
        new AppConfig { LastServerHost = host, LastServerPort = port, LastServerUseSsl = false }.Save(AppPaths.ConfigPath());
        var vm = new MainWindowViewModel { ServerHost = host, ServerPort = port.ToString() };
        RestoreFlow(vm, Mnemonic, null, name);
        await Eventually(() => !vm.IsSyncing && vm.IsConnected && vm.Coins.Count(c => c.IsSelectable && !Used.Contains(c.Txid + ":" + c.Vout)) >= 3,
            timeoutMs: 60_000,
            describe: () => $"syncing={vm.IsSyncing} connected={vm.IsConnected} coins={vm.Coins.Count} status='{vm.StatusMessage}'");
        return vm;
    }

    private static void AddContact(MainWindowViewModel vm, string name, string address)
    {
        vm.NewContactName = name;
        vm.NewContactAddress = address;
        vm.AddContactCommand.Execute(null);
    }

    /// <summary>Pays 0.1 from one coin that is not <paramref name="used"/> to what the form holds, and checks the node took it.</summary>
    private static async Task PayFromForm(MainWindowViewModel vm)
    {
        vm.ClearCoinSelectionCommand.Execute(null);
        var coin = vm.Coins.First(c => c.IsSelectable && !Used.Contains(c.Txid + ":" + c.Vout));
        lock (Used) Used.Add(coin.Txid + ":" + coin.Vout);
        vm.ToggleCoinSelectedCommand.Execute(coin);
        vm.SendAll = false;
        vm.SendAmount = "0.1";
        vm.SendFeeRate = "2";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingSend, vm.SendPreview);
        await vm.ConfirmSendCommand.ExecuteAsync(null);
        Assert.StartsWith(Loc.Tr("msg.broadcasted"), vm.SendPreview);
        Assert.False(vm.HasPendingSend, vm.SendPreview);
    }

    [Fact]
    public Task Each_saved_contact_can_be_paid_from_the_send_tab() => UiSession.Run(async () =>
    {
        if (!Enabled) return;
        var vm = await Open("contacts-pay");
        for (var i = 0; i < 3; i++)
            AddContact(vm, $"contact {i}", Addresses[i]);
        Assert.Equal(3, vm.Contacts.Count);

        foreach (var contact in vm.Contacts.ToList())
        {
            vm.SendToContact = contact;                                   // the picker in the Send tab
            Assert.Equal(contact.Address, vm.SendTo);
            await PayFromForm(vm);
            await Eventually(() => !vm.IsSyncing && vm.IsConnected, timeoutMs: 60_000);
        }
    });

    [Fact]
    public Task The_same_contact_can_be_paid_twice_in_a_row() => UiSession.Run(async () =>
    {
        if (!Enabled) return;
        var vm = await Open("contacts-twice");
        AddContact(vm, "alice", Addresses[0]);
        var alice = vm.Contacts.Single();

        // Through the real view: the Send tab's contact picker and recipient box.
        var window = new Window { Width = 1020, Height = 680, Content = new PalladiumWallet.App.Views.MainView { DataContext = vm } };
        window.Show();
        vm.SelectedTabIndex = 1; // Send
        Dispatcher.UIThread.RunJobs();
        var picker = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && c.ItemsSource == vm.Contacts);
        var recipient = window.GetVisualDescendants().OfType<TextBox>()
            .First(t => t.IsEffectivelyVisible && t.PlaceholderText == Loc.Tr("send.to"));

        picker.SelectedItem = alice;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Addresses[0], recipient.Text);
        await PayFromForm(vm);
        await Eventually(() => !vm.IsSyncing && vm.IsConnected, timeoutMs: 60_000);
        Assert.Equal("", recipient.Text);                  // the form was cleared by the send

        // Second payment to the same contact: the picker must fill the address again.
        picker.SelectedItem = alice;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Addresses[0], recipient.Text);
        await PayFromForm(vm);
        window.Close();
    });

    [Fact]
    public Task A_contact_added_while_the_wallet_syncs_is_kept_and_payable() => UiSession.Run(async () =>
    {
        if (!Enabled) return;
        var vm = await Open("contacts-sync");
        AddContact(vm, "first", Addresses[0]);
        vm.SendToContact = vm.Contacts[0];
        await PayFromForm(vm);              // FinishSend starts a resync

        // Right after a send the wallet resyncs: adding contacts at that moment saves the document while the sync works on it.
        AddContact(vm, "second", Addresses[1]);
        AddContact(vm, "third", Addresses[2]);
        Assert.Equal(3, vm.Contacts.Count);
        await Eventually(() => !vm.IsSyncing && vm.IsConnected, timeoutMs: 60_000);

        // The contacts are in the wallet file, not just in memory.
        var file = AppPaths.WalletFiles(NetKind.Regtest).Single(f => f.Contains("contacts-sync"));
        var stored = WalletStore.Load(file);
        Assert.Equal(new[] { Addresses[0], Addresses[1], Addresses[2] }, stored.Contacts.Select(c => c.Address).ToArray());

        vm.SendToContact = vm.Contacts[2];
        Assert.Equal(Addresses[2], vm.SendTo);
        await PayFromForm(vm);
    });
}
