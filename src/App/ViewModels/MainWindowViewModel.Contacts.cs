using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NBitcoin;
using PalladiumWallet.App.Localization;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Storage;

namespace PalladiumWallet.App.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<ContactEntry> Contacts { get; } = [];

    [ObservableProperty]
    private ContactEntry? selectedContactInList;

    /// <summary>Contact selected in the Send panel's ComboBox: fills SendTo.</summary>
    [ObservableProperty]
    private ContactEntry? sendToContact;

    partial void OnSendToContactChanged(ContactEntry? value)
    {
        if (value is not null)
            SendTo = value.Address;
    }

    [ObservableProperty]
    private string newContactName = "";

    [ObservableProperty]
    private string newContactAddress = "";

    /// <summary>Why the contact was not saved, shown under the form (the status bar also carries it).</summary>
    [ObservableProperty]
    private string contactFormMessage = "";

    partial void OnNewContactNameChanged(string value) => ContactFormMessage = "";

    partial void OnNewContactAddressChanged(string value) => ContactFormMessage = "";

    [RelayCommand]
    private void AddContact()
    {
        var name = NewContactName.Trim();
        // Pasted addresses often carry spaces or line breaks anywhere: drop all whitespace.
        var addr = string.Concat(NewContactAddress.Where(c => !char.IsWhiteSpace(c)));
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(addr)) return;
        try
        {
            BitcoinAddress.Create(addr, PalladiumNetworks.For(Net));
        }
        catch (System.FormatException)
        {
            StatusMessage = ContactFormMessage = Loc.Tr("msg.contact.address.invalid");
            return;
        }
        if (Contacts.Any(c => string.Equals(c.Address, addr, System.StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = ContactFormMessage = Loc.Tr("msg.contact.duplicate");
            return;
        }
        Contacts.Add(new ContactEntry(name, addr));
        StatusMessage = ""; // an earlier "invalid address" for this form no longer applies
        NewContactName = NewContactAddress = "";
        PersistContacts();
    }

    [RelayCommand]
    private void RemoveSelectedContact()
    {
        if (SelectedContactInList is { } c)
        {
            Contacts.Remove(c);
            SelectedContactInList = null;
            PersistContacts();
        }
    }

    private void PersistContacts()
    {
        if (_doc is null || _walletPath is null) return;
        _doc.Contacts = Contacts
            .Select(c => new StoredContact { Name = c.Name, Address = c.Address })
            .ToList();
        WalletStore.Save(_doc, _walletPath, _password);
    }

    private void LoadContacts()
    {
        Contacts.Clear();
        if (_doc is null) return;
        foreach (var c in _doc.Contacts)
            Contacts.Add(new ContactEntry(c.Name, c.Address));
    }
}
