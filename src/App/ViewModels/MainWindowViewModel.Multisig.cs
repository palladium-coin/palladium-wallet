using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalladiumWallet.App.Localization;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.App.ViewModels;

public partial class MainWindowViewModel
{
    // ---- creating a multisig wallet (ADR 0058); the wallet is opened and spent from like any other (ADR 0056)

    [ObservableProperty]
    private string multisigThresholdInput = "2";

    /// <summary>All N cosigner keys, one per line, this wallet's own included.</summary>
    [ObservableProperty]
    private string multisigKeysInput = "";

    [ObservableProperty]
    private bool multisigWrapped;

    /// <summary>The seed of this wallet's own key; empty for a wallet that only coordinates (cannot sign).</summary>
    [ObservableProperty]
    private string multisigMnemonicInput = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultisigOwnKey))]
    private string multisigOwnKey = "";

    public bool HasMultisigOwnKey => MultisigOwnKey.Length > 0;

    [ObservableProperty]
    private string multisigWalletId = "";

    /// <summary>The ID the other cosigners read; empty means the wallet is created without the comparison.</summary>
    [ObservableProperty]
    private string multisigExpectedIdInput = "";

    private ScriptKind MultisigKind => MultisigWrapped ? ScriptKind.WrappedSegwitMultisig : ScriptKind.NativeSegwitMultisig;

    private void ResetMultisigInputs()
    {
        MultisigThresholdInput = "2";
        MultisigKeysInput = MultisigMnemonicInput = MultisigOwnKey = MultisigWalletId = MultisigExpectedIdInput = "";
        MultisigWrapped = false;
    }

    // The type changes every key (BIP48 script type), so a key shown for the other type no longer applies.
    partial void OnMultisigWrappedChanged(bool value) => MultisigOwnKey = "";

    [RelayCommand]
    private void WizardStartMultisig()
    {
        _wizardFlow = WizardFlowKind.Multisig;
        ResetMultisigInputs();
        SetupStep = StepMultisig;
        StatusMessage = "";
    }

    [RelayCommand]
    private void GenerateMultisigSeed()
    {
        // Kept in the wallet file (encrypted), so it can be read again under Wallet information.
        MultisigMnemonicInput = Bip39.Generate(MnemonicLength.Twelve).ToString();
        MultisigOwnKey = "";
    }

    /// <summary>The key this seed gives to the wallet: what the other cosigners are handed to enter.</summary>
    [RelayCommand]
    private void ShowMultisigOwnKey()
    {
        try
        {
            MultisigOwnKey = Cosigner.FromMnemonic(MultisigMnemonicInput.Trim(), null, MultisigKind, Profile).ToText(MultisigKind, Profile);
            StatusMessage = "";
        }
        catch (ArgumentException)
        {
            MultisigOwnKey = "";
            StatusMessage = Loc.Tr("msg.words.invalid");
        }
    }

    private string[] MultisigKeyLines() =>
        MultisigKeysInput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private (WalletDocument Doc, MultisigAccount Account) NewMultisigFromInputs()
    {
        if (!int.TryParse(MultisigThresholdInput.Trim(), out var threshold))
            throw new InvalidOperationException(Loc.Tr("msg.multisig.threshold"));
        var words = string.IsNullOrWhiteSpace(MultisigMnemonicInput) ? null : MultisigMnemonicInput.Trim();
        return WalletLoader.NewMultisig(threshold, MultisigKeyLines(), words, null, Profile, MultisigKind);
    }

    [RelayCommand]
    private void WizardNextFromMultisig()
    {
        try
        {
            MultisigWalletId = NewMultisigFromInputs().Account.WalletId;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            StatusMessage = ex is InvalidOperationException ? ex.Message : $"{Loc.Tr("msg.error")}: {ex.Message}";
            return;
        }
        MultisigExpectedIdInput = "";
        SetupStep = StepMultisigVerify;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardNextFromMultisigVerify()
    {
        var expected = MultisigExpectedIdInput.Trim();
        if (expected.Length > 0 && !string.Equals(expected, MultisigWalletId, StringComparison.OrdinalIgnoreCase))
        {
            // Nothing is written: these keys are not the ones the others see.
            StatusMessage = string.Format(Loc.Tr("msg.multisig.idmismatch"), MultisigWalletId, expected);
            return;
        }
        PasswordInput = ConfirmPasswordInput = "";
        EncryptWallet = true;
        SetupStep = StepPassword;
        StatusMessage = expected.Length > 0 ? "" : Loc.Tr("msg.multisig.unverified");
    }
}
