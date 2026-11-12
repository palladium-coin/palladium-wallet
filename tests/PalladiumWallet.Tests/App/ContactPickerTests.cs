using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// The Send tab's contact picker, through the real view: choosing a contact fills the recipient box, and choosing
/// the same contact again after the box was cleared (a finished send) or edited fills it again.
/// </summary>
[Collection("AppPaths")]
public sealed class ContactPickerTests : VmTestBase
{
    private const string Alice = "bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq";   // any string: the picker does not validate

    private static (PalladiumWallet.App.Views.MainView View, Window Window, ComboBox Picker, TextBox Recipient) Show(MainWindowViewModel vm)
    {
        var view = new PalladiumWallet.App.Views.MainView { DataContext = vm };
        var window = new Window { Width = 1020, Height = 680, Content = view };
        window.Show();
        vm.SelectedTabIndex = 1; // Send
        Dispatcher.UIThread.RunJobs();
        var picker = view.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && c.ItemsSource == vm.Contacts);
        var recipient = view.GetVisualDescendants().OfType<TextBox>()
            .First(t => t.IsEffectivelyVisible && t.PlaceholderText == Loc.Tr("send.to"));
        return (view, window, picker, recipient);
    }

    private static MainWindowViewModel OpenWithContacts()
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "picker");
        vm.Contacts.Add(new ContactEntry("alice", Alice));
        vm.Contacts.Add(new ContactEntry("bob", "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4"));
        return vm;
    }

    [Fact]
    public Task Picking_a_contact_in_the_view_fills_the_recipient_box() => UiSession.Run(() =>
    {
        var vm = OpenWithContacts();
        var (_, window, picker, recipient) = Show(vm);
        picker.SelectedItem = vm.Contacts[0];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Alice, recipient.Text);
        window.Close();
    });

    [Fact]
    public Task The_same_contact_can_be_picked_again_after_the_box_was_cleared() => UiSession.Run(() =>
    {
        var vm = OpenWithContacts();
        var (_, window, picker, recipient) = Show(vm);
        picker.SelectedItem = vm.Contacts[0];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Alice, recipient.Text);

        vm.SendTo = ""; // what a finished send does
        Dispatcher.UIThread.RunJobs();
        Assert.Null(picker.SelectedItem);                 // the picker no longer claims a contact the box does not hold
        Assert.Equal("", recipient.Text);

        picker.SelectedItem = vm.Contacts[0];             // the same contact again
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Alice, recipient.Text);
        window.Close();
    });

    [Fact]
    public Task The_same_contact_can_be_picked_again_after_the_box_was_edited_by_hand() => UiSession.Run(() =>
    {
        var vm = OpenWithContacts();
        vm.SendToContact = vm.Contacts[0];
        Assert.Equal(Alice, vm.SendTo);

        vm.SendTo = "something typed by hand";
        Assert.Null(vm.SendToContact);

        vm.SendToContact = vm.Contacts[0];
        Assert.Equal(Alice, vm.SendTo);
    });

    [Fact]
    public Task Picking_another_contact_replaces_the_address_and_keeps_the_pick() => UiSession.Run(() =>
    {
        var vm = OpenWithContacts();
        vm.SendToContact = vm.Contacts[0];
        vm.SendToContact = vm.Contacts[1];
        Assert.Equal(vm.Contacts[1].Address, vm.SendTo);
        Assert.Same(vm.Contacts[1], vm.SendToContact);    // the replacement must not clear its own pick
    });

    [Fact]
    public Task A_pasted_address_that_differs_only_in_case_keeps_the_pick() => UiSession.Run(() =>
    {
        var vm = OpenWithContacts();
        vm.SendToContact = vm.Contacts[0];
        vm.SendTo = Alice.ToUpperInvariant();             // bech32 is case-insensitive
        Assert.Same(vm.Contacts[0], vm.SendToContact);
    });

    [Fact]
    public Task Contacts_of_an_encrypted_wallet_are_saved_and_come_back_after_reopening() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, "correct horse battery", "enc-contacts");
        vm.NewContactName = "alice";
        // A valid Palladium mainnet address (the form validates it), pasted with a space and a line break.
        var address = PalladiumWallet.Core.Crypto.HdAccount.FromMnemonic(ParseMnemonic(), null,
            PalladiumWallet.Core.Chain.ScriptKind.NativeSegwit, PalladiumWallet.Core.Chain.ChainProfiles.Mainnet)
            .GetReceiveAddress(3).ToString();
        vm.NewContactAddress = " " + address[..20] + "\n" + address[20..];
        vm.AddContactCommand.Execute(null);
        Assert.Single(vm.Contacts);
        Assert.Equal(address, vm.Contacts[0].Address);

        var file = PalladiumWallet.Core.Storage.AppPaths.WalletFiles(PalladiumWallet.Core.Chain.NetKind.Mainnet).Single(f => f.Contains("enc-contacts"));
        Assert.True(PalladiumWallet.Core.Storage.WalletStore.RequiresPassword(file));
        var stored = PalladiumWallet.Core.Storage.WalletStore.Load(file, "correct horse battery");
        Assert.Equal(address, stored.Contacts.Single().Address);
    });
}
