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

    /// <summary>Optional BIP39 passphrase of the own seed.</summary>
    [ObservableProperty]
    private string multisigPassphraseInput = "";

    /// <summary>The account number the own key is derived at (BIP48 <c>account'</c>); 0 unless the seed serves several wallets.</summary>
    [ObservableProperty]
    private string multisigAccountInput = "0";

    /// <summary>Words typed again to confirm a seed that was generated here (shown only for a generated seed).</summary>
    [ObservableProperty]
    private string multisigConfirmWordsInput = "";

    [ObservableProperty]
    private bool multisigSeedGenerated;

    private bool _settingGeneratedSeed;

    // A seed the user types or pastes is theirs already (written down elsewhere): only a generated one is confirmed.
    partial void OnMultisigMnemonicInputChanged(string value)
    {
        if (!_settingGeneratedSeed)
            MultisigSeedGenerated = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultisigOwnKey))]
    private string multisigOwnKey = "";

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? multisigOwnKeyQr;

    // The key as a QR, for a device or a person that scans it instead of typing 110 characters.
    partial void OnMultisigOwnKeyChanged(string value)
    {
        var previous = MultisigOwnKeyQr;
        MultisigOwnKeyQr = string.IsNullOrEmpty(value) ? null : GenerateQr(value);
        previous?.Dispose();
    }

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
        MultisigPassphraseInput = MultisigConfirmWordsInput = "";
        MultisigAccountInput = "0";
        MultisigSeedGenerated = false;
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
        _settingGeneratedSeed = true;
        MultisigMnemonicInput = Bip39.Generate(MnemonicLength.Twelve).ToString();
        _settingGeneratedSeed = false;
        MultisigSeedGenerated = true;
        MultisigConfirmWordsInput = "";
        MultisigOwnKey = "";
    }

    /// <summary>The key this seed gives to the wallet: what the other cosigners are handed to enter.</summary>
    [RelayCommand]
    private void ShowMultisigOwnKey()
    {
        if (!int.TryParse(MultisigAccountInput.Trim(), out var account) || account < 0)
        {
            MultisigOwnKey = "";
            StatusMessage = Loc.Tr("msg.multisig.account");
            return;
        }
        try
        {
            var passphrase = string.IsNullOrEmpty(MultisigPassphraseInput) ? null : MultisigPassphraseInput;
            MultisigOwnKey = Cosigner.FromMnemonic(MultisigMnemonicInput.Trim(), passphrase, MultisigKind, Profile, account).ToText(MultisigKind, Profile);
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
        var passphrase = string.IsNullOrEmpty(MultisigPassphraseInput) ? null : MultisigPassphraseInput;
        return WalletLoader.NewMultisig(threshold, MultisigKeyLines(), words, passphrase, Profile, MultisigKind);
    }

    [RelayCommand]
    private void WizardNextFromMultisig()
    {
        if (MultisigSeedGenerated)
        {
            var typed = string.Join(' ', MultisigConfirmWordsInput.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (!string.Equals(typed, MultisigMnemonicInput, StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = Loc.Tr("msg.seed.mismatch");
                return;
            }
        }
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

    /// <summary>Fills the step from a configuration file another tool exported (ADR 0061); the keys can then be checked and the seed added.</summary>
    public void ApplyMultisigConfig(string text)
    {
        try
        {
            var (threshold, kind, cosigners) = MultisigConfig.Parse(text, Profile);
            MultisigThresholdInput = threshold.ToString();
            MultisigWrapped = kind == ScriptKind.WrappedSegwitMultisig;
            MultisigKeysInput = string.Join("\n", cosigners);
            StatusMessage = "";
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = $"{Loc.Tr("msg.multisig.config.invalid")}: {ex.Message}";
        }
    }
}
