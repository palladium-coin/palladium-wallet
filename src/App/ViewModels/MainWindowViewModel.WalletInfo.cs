using PalladiumWallet.Core.Crypto;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalladiumWallet.App.Localization;

namespace PalladiumWallet.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private bool isWalletInfoOpen;

    [ObservableProperty]
    private string walletInfoSeedPasswordInput = "";

    [ObservableProperty]
    private bool isWalletInfoSeedRevealed;

    [ObservableProperty]
    private string walletInfoSeedError = "";

    // ---- computed wallet info (populated when overlay opens) ----

    public string WalletInfoFileName =>
        _walletPath is null ? "" :
        Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(_walletPath));

    public string WalletInfoNetwork => _doc?.Network ?? "";

    public string WalletInfoType
    {
        get
        {
            if (_doc is null) return "";
            if (_doc.Mnemonic is not null) return Loc.Tr("walletinfo.type.seed");
            if (_doc.AccountXprv is not null) return Loc.Tr("walletinfo.type.xprv");
            if (_doc.WifKeys is { Count: > 0 }) return Loc.Tr("walletinfo.type.wif");
            return Loc.Tr("walletinfo.type.watchonly");
        }
    }

    public string WalletInfoScriptKind => _doc?.ScriptKind ?? "";

    public string WalletInfoDerivPath => _doc?.AccountPath ?? "";

    public string WalletInfoXpub => _doc?.AccountXpub ?? "";

    /// <summary>
    /// Multisig wallets: "2-of-3 · ID zdl27u8e". The ID covers every cosigner key and the threshold: the cosigners compare
    /// it with each other, since the wallet cannot authenticate a key by itself (ADR 0057). Empty for other wallets.
    /// </summary>
    public string WalletInfoMultisigId => _account is MultisigAccount m
        ? $"{m.Threshold}-of-{m.Cosigners.Count} · ID {m.WalletId}"
        : "";

    public string WalletInfoFingerprint => _doc?.MasterFingerprint ?? "";

    public bool WalletInfoHasSeed => _doc?.Mnemonic is not null;

    public bool WalletInfoSeedNeedsPassword => !string.IsNullOrEmpty(_password);

    public string WalletInfoSeedText => IsWalletInfoSeedRevealed ? (_doc?.Mnemonic ?? "") : "";

    public bool WalletInfoHasPassphrase => !string.IsNullOrEmpty(_doc?.Passphrase);

    [RelayCommand]
    private void OpenWalletInfo()
    {
        WalletInfoSeedPasswordInput = "";
        IsWalletInfoSeedRevealed = false;
        WalletInfoSeedError = "";
        OnPropertyChanged(nameof(WalletInfoFileName));
        OnPropertyChanged(nameof(WalletInfoNetwork));
        OnPropertyChanged(nameof(WalletInfoType));
        OnPropertyChanged(nameof(WalletInfoScriptKind));
        OnPropertyChanged(nameof(WalletInfoDerivPath));
        OnPropertyChanged(nameof(WalletInfoXpub));
        OnPropertyChanged(nameof(WalletInfoMultisigId));
        OnPropertyChanged(nameof(WalletInfoFingerprint));
        OnPropertyChanged(nameof(WalletInfoHasSeed));
        OnPropertyChanged(nameof(WalletInfoSeedNeedsPassword));
        OnPropertyChanged(nameof(WalletInfoHasPassphrase));
        IsWalletInfoOpen = true;
    }

    [RelayCommand]
    private void CloseWalletInfo()
    {
        IsWalletInfoOpen = false;
        WalletInfoSeedPasswordInput = "";
        IsWalletInfoSeedRevealed = false;
        WalletInfoSeedError = "";
        OnPropertyChanged(nameof(WalletInfoSeedText));
    }

    [RelayCommand]
    private void RevealSeed()
    {
        if (_doc?.Mnemonic is null) return;

        if (WalletInfoSeedNeedsPassword)
        {
            if (WalletInfoSeedPasswordInput != _password)
            {
                WalletInfoSeedError = Loc.Tr("msg.wrongpassword");
                return;
            }
        }

        WalletInfoSeedError = "";
        IsWalletInfoSeedRevealed = true;
        OnPropertyChanged(nameof(WalletInfoSeedText));
    }

    [RelayCommand]
    private void HideSeed()
    {
        IsWalletInfoSeedRevealed = false;
        WalletInfoSeedPasswordInput = "";
        WalletInfoSeedError = "";
        OnPropertyChanged(nameof(WalletInfoSeedText));
    }
}
