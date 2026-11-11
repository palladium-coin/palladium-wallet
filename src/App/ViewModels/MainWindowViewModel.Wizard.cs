using System;
using System.Collections.ObjectModel;
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
    // ---- setup wizard

    public const string StepDataLocation = "data-location";
    public const string StepStart = "start";
    public const string StepChooseWallet = "choose-wallet";
    public const string StepOpen = "open";
    public const string StepShowSeed = "show-seed";
    public const string StepConfirmSeed = "confirm-seed";
    public const string StepWords = "words";
    public const string StepPassphrase = "passphrase";
    public const string StepScriptType = "script-type";
    public const string StepImportXkey = "import-xkey";
    public const string StepImportWif = "import-wif";
    public const string StepImportAddress = "import-address";
    public const string StepPassword = "password";
    public const string StepMultisig = "multisig";
    public const string StepMultisigVerify = "multisig-verify";

    private enum WizardFlowKind { New, Restore, ImportXkey, ImportWif, ImportAddress, Multisig }
    private WizardFlowKind _wizardFlow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStepDataLocation))]
    [NotifyPropertyChangedFor(nameof(IsStepStart))]
    [NotifyPropertyChangedFor(nameof(IsStepChooseWallet))]
    [NotifyPropertyChangedFor(nameof(IsStepOpen))]
    [NotifyPropertyChangedFor(nameof(IsStepShowSeed))]
    [NotifyPropertyChangedFor(nameof(IsStepConfirmSeed))]
    [NotifyPropertyChangedFor(nameof(IsStepWords))]
    [NotifyPropertyChangedFor(nameof(IsStepPassphrase))]
    [NotifyPropertyChangedFor(nameof(IsStepScriptType))]
    [NotifyPropertyChangedFor(nameof(IsStepImportXkey))]
    [NotifyPropertyChangedFor(nameof(IsStepImportWif))]
    [NotifyPropertyChangedFor(nameof(IsStepImportAddress))]
    [NotifyPropertyChangedFor(nameof(IsStepPassword))]
    [NotifyPropertyChangedFor(nameof(IsStepMultisig))]
    [NotifyPropertyChangedFor(nameof(IsStepMultisigVerify))]
    private string setupStep = StepStart;

    // A message belongs to the screen that raised it: leaving a step, or toggling the option the message
    // is about, must not leave it on the status bar.
    partial void OnSetupStepChanged(string value)
    {
        StatusMessage = "";
        OnPropertyChanged(nameof(WizardStepText));
        OnPropertyChanged(nameof(HasWizardStepText));
    }

    private string[] WizardFlowSteps() => _wizardFlow switch
    {
        WizardFlowKind.New => [StepShowSeed, StepConfirmSeed, StepPassphrase, StepScriptType, StepPassword],
        WizardFlowKind.Restore => [StepWords, StepPassphrase, StepScriptType, StepPassword],
        WizardFlowKind.ImportXkey => [StepImportXkey, StepPassword],
        WizardFlowKind.ImportWif => [StepImportWif, StepScriptType, StepPassword],
        WizardFlowKind.ImportAddress => [StepImportAddress, StepPassword],
        WizardFlowKind.Multisig => [StepMultisig, StepMultisigVerify, StepPassword],
        _ => [],
    };

    /// <summary>"Step 2 of 4" while a creation flow is on screen; empty on the start, open and data-location screens.</summary>
    public string WizardStepText
    {
        get
        {
            var steps = WizardFlowSteps();
            var index = Array.IndexOf(steps, SetupStep);
            return index >= 0 && steps.Length > 1 ? string.Format(Loc.Tr("wiz.step"), index + 1, steps.Length) : "";
        }
    }

    public bool HasWizardStepText => WizardStepText.Length > 0;

    public bool IsStepDataLocation => SetupStep == StepDataLocation;
    public bool IsStepStart        => SetupStep == StepStart;
    public bool IsStepChooseWallet => SetupStep == StepChooseWallet;
    public bool IsStepOpen         => SetupStep == StepOpen;
    public bool IsStepShowSeed     => SetupStep == StepShowSeed;
    public bool IsStepConfirmSeed  => SetupStep == StepConfirmSeed;
    public bool IsStepWords        => SetupStep == StepWords;
    public bool IsStepPassphrase   => SetupStep == StepPassphrase;
    public bool IsStepScriptType   => SetupStep == StepScriptType;
    public bool IsStepImportXkey    => SetupStep == StepImportXkey;
    public bool IsStepImportWif     => SetupStep == StepImportWif;
    public bool IsStepImportAddress => SetupStep == StepImportAddress;
    public bool IsStepPassword      => SetupStep == StepPassword;
    public bool IsStepMultisig      => SetupStep == StepMultisig;
    public bool IsStepMultisigVerify => SetupStep == StepMultisigVerify;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLegacySelected))]
    [NotifyPropertyChangedFor(nameof(IsWrappedSegwitSelected))]
    [NotifyPropertyChangedFor(nameof(IsNativeSegwitSelected))]
    [NotifyPropertyChangedFor(nameof(IsTaprootSelected))]
    private ScriptKind selectedScriptKind = ScriptKind.NativeSegwit;

    public bool IsLegacySelected        => SelectedScriptKind == ScriptKind.Legacy;
    public bool IsWrappedSegwitSelected => SelectedScriptKind == ScriptKind.WrappedSegwit;
    public bool IsNativeSegwitSelected  => SelectedScriptKind == ScriptKind.NativeSegwit;
    public bool IsTaprootSelected       => SelectedScriptKind == ScriptKind.Taproot;

    [RelayCommand] private void SelectLegacy()        => SelectedScriptKind = ScriptKind.Legacy;
    [RelayCommand] private void SelectWrappedSegwit() => SelectedScriptKind = ScriptKind.WrappedSegwit;
    [RelayCommand] private void SelectNativeSegwit()  => SelectedScriptKind = ScriptKind.NativeSegwit;
    [RelayCommand] private void SelectTaproot()       => SelectedScriptKind = ScriptKind.Taproot;

    public string DefaultDataPath => AppPaths.DefaultDataRoot();

    [ObservableProperty]
    private string importXkeyInput = "";

    /// <summary>Optional origin of an imported xpub: master fingerprint (8 hex digits), for PSBTs an external signer can match.</summary>
    [ObservableProperty]
    private string importFingerprintInput = "";

    /// <summary>Optional account path of the imported key (e.g. m/84'/746'/0').</summary>
    [ObservableProperty]
    private string importPathInput = "";

    [ObservableProperty]
    private string importWifInput = "";

    [ObservableProperty]
    private string importAddressInput = "";

    // Script type detected during xkey decoding (to display to the user)
    [ObservableProperty]
    private string importXkeyDetectedKind = "";

    [ObservableProperty]
    private string mnemonicInput = "";

    [ObservableProperty]
    private string confirmMnemonicInput = "";

    [ObservableProperty]
    private string passphraseInput = "";

    [ObservableProperty]
    private string walletNameInput = "";

    [ObservableProperty]
    private string passwordInput = "";

    [ObservableProperty]
    private string confirmPasswordInput = "";

    [ObservableProperty]
    private bool encryptWallet = true;

    partial void OnEncryptWalletChanged(bool value) => StatusMessage = ""; // "passwords do not match" is moot once encryption is off

    [ObservableProperty]
    private bool walletFileExists;

    public ObservableCollection<WalletFileEntry> WalletList { get; } = [];

    [RelayCommand]
    private void UseDefaultDataLocation() => ApplyDataLocation(AppPaths.DefaultDataRoot());

    public void ApplyDataLocation(string root)
    {
        try
        {
            AppPaths.ConfigureDataLocation(root);
        }
        catch (Exception ex)
        {
            StatusMessage = $"{Loc.Tr("msg.error")}: {ex.Message}";
            return;
        }
        _config = AppConfig.Load();
        _loc = Loc.SwitchTo(_config.Language);
        OnPropertyChanged(nameof(Loc));
        OnPropertyChanged(nameof(UnitLabel));
        RefreshSetupState();
    }

    private void RefreshSetupState()
    {
        SetupStep = StepStart;
        SelectedScriptKind = ScriptKind.NativeSegwit;
        MnemonicInput = ConfirmMnemonicInput = PassphraseInput = PasswordInput = ConfirmPasswordInput = "";
        WalletFileExists = AppPaths.WalletFiles(Net).Count > 0;
        StatusMessage = "";
        RefreshServers();
        _ = ConnectAndSync();
    }

    [RelayCommand]
    private void WizardStartOpen()
    {
        var files = AppPaths.WalletFiles(Net);
        if (files.Count > 1)
        {
            WalletList.Clear();
            foreach (var path in files)
                WalletList.Add(new WalletFileEntry(Path.GetFileName(path), path));
            SetupStep = StepChooseWallet;
            StatusMessage = "";
            return;
        }
        _pendingOpenPath = files.Count == 1 ? files[0] : AppPaths.DefaultWalletPath(Net);
        PasswordInput = "";
        SetupStep = StepOpen;
        StatusMessage = "";
    }

    [RelayCommand]
    private void ChooseWallet(WalletFileEntry? entry)
    {
        if (entry is null)
            return;
        _pendingOpenPath = entry.Path;
        PasswordInput = "";
        SetupStep = StepOpen;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardStartNew()
    {
        _wizardFlow = WizardFlowKind.New;
        MnemonicInput = Bip39.Generate(MnemonicLength.Twelve).ToString();
        SetupStep = StepShowSeed;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardStartRestore()
    {
        _wizardFlow = WizardFlowKind.Restore;
        MnemonicInput = "";
        SetupStep = StepWords;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardStartImportXkey()
    {
        _wizardFlow = WizardFlowKind.ImportXkey;
        ImportXkeyInput = "";
        ImportFingerprintInput = ImportPathInput = "";
        ImportXkeyDetectedKind = "";
        SetupStep = StepImportXkey;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardStartImportWif()
    {
        _wizardFlow = WizardFlowKind.ImportWif;
        ImportWifInput = "";
        SetupStep = StepImportWif;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardStartImportAddress()
    {
        _wizardFlow = WizardFlowKind.ImportAddress;
        ImportAddressInput = "";
        SetupStep = StepImportAddress;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardNextFromShowSeed()
    {
        ConfirmMnemonicInput = "";
        SetupStep = StepConfirmSeed;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardNextFromConfirmSeed()
    {
        var normalized = string.Join(' ',
            ConfirmMnemonicInput.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!string.Equals(normalized, MnemonicInput, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = Loc.Tr("msg.seed.mismatch");
            return;
        }
        GoToPassphraseStep();
    }

    [RelayCommand]
    private void WizardNextFromWords()
    {
        if (!Bip39.TryParse(MnemonicInput, out _))
        {
            StatusMessage = Loc.Tr("msg.words.invalid");
            return;
        }
        GoToPassphraseStep();
    }

    private void GoToPassphraseStep()
    {
        PassphraseInput = "";
        SetupStep = StepPassphrase;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardNextFromPassphrase()
    {
        SetupStep = StepScriptType;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardNextFromImportXkey()
    {
        if (string.IsNullOrWhiteSpace(ImportXkeyInput))
        {
            StatusMessage = Loc.Tr("msg.xkey.required");
            return;
        }
        // Validate and detect the type: try as xpub first, then as xprv.
        if (Slip132.TryDecodePublic(ImportXkeyInput.Trim(), Profile, out _, out var pubKind))
        {
            SelectedScriptKind = pubKind;
            ImportXkeyDetectedKind = $"{pubKind} (watch-only)";
        }
        else if (Slip132.TryDecodePrivate(ImportXkeyInput.Trim(), Profile, out _, out var prvKind))
        {
            SelectedScriptKind = prvKind;
            ImportXkeyDetectedKind = prvKind.ToString();
        }
        else
        {
            StatusMessage = Loc.Tr("msg.xkey.invalid");
            return;
        }
        if (!TryReadImportOrigin(out _, out _))
            return;
        PasswordInput = ConfirmPasswordInput = "";
        EncryptWallet = true;
        SetupStep = StepPassword;
        StatusMessage = "";
    }

    /// <summary>
    /// Reads the optional fingerprint and path fields. Empty means "not given" (no BIP32 origin in
    /// exported PSBTs); a non-empty field that does not parse sets the status message and fails.
    /// </summary>
    private bool TryReadImportOrigin(out NBitcoin.HDFingerprint? fingerprint, out NBitcoin.KeyPath? path)
    {
        fingerprint = null;
        path = null;
        if (!string.IsNullOrWhiteSpace(ImportFingerprintInput))
        {
            fingerprint = HdAccount.ParseFingerprint(ImportFingerprintInput.Trim().ToLowerInvariant());
            if (fingerprint is null)
            {
                StatusMessage = Loc.Tr("msg.xkey.fingerprint.invalid");
                return false;
            }
        }
        if (!string.IsNullOrWhiteSpace(ImportPathInput)
            && !DerivationPaths.TryParse(ImportPathInput, out path))
        {
            StatusMessage = Loc.Tr("msg.xkey.path.invalid");
            return false;
        }
        return true;
    }

    [RelayCommand]
    private void WizardNextFromImportWif()
    {
        if (string.IsNullOrWhiteSpace(ImportWifInput))
        {
            StatusMessage = Loc.Tr("msg.wif.required");
            return;
        }
        // Validate the WIF with an early parse.
        try
        {
            _ = new NBitcoin.BitcoinSecret(ImportWifInput.Trim(), PalladiumNetworks.For(Net));
        }
        catch
        {
            StatusMessage = Loc.Tr("msg.wif.invalid");
            return;
        }
        SetupStep = StepScriptType;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardNextFromImportAddress()
    {
        var addresses = ImportAddressInput.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (addresses.Length == 0)
        {
            StatusMessage = Loc.Tr("msg.address.required");
            return;
        }
        var network = PalladiumNetworks.For(Net);
        foreach (var a in addresses)
        {
            try
            {
                _ = NBitcoin.BitcoinAddress.Create(a, network);
            }
            catch
            {
                StatusMessage = Loc.Tr("msg.address.invalid");
                return;
            }
        }
        PasswordInput = ConfirmPasswordInput = "";
        EncryptWallet = true;
        SetupStep = StepPassword;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardNextFromScriptType()
    {
        PasswordInput = ConfirmPasswordInput = "";
        EncryptWallet = true;
        SetupStep = StepPassword;
        StatusMessage = "";
    }

    [RelayCommand]
    private void WizardBack()
    {
        SetupStep = SetupStep switch
        {
            StepOpen => WalletList.Count > 1 ? StepChooseWallet : StepStart,
            StepChooseWallet or StepShowSeed or StepWords or StepImportXkey or StepImportWif or StepImportAddress or StepMultisig => StepStart,
            StepMultisigVerify => StepMultisig,
            StepConfirmSeed => StepShowSeed,
            StepPassphrase => _wizardFlow == WizardFlowKind.Restore ? StepWords : StepConfirmSeed,
            StepScriptType => _wizardFlow == WizardFlowKind.ImportWif ? StepImportWif : StepPassphrase,
            StepPassword => _wizardFlow switch
            {
                WizardFlowKind.ImportXkey => StepImportXkey,
                WizardFlowKind.ImportAddress => StepImportAddress,
                WizardFlowKind.Multisig => StepMultisigVerify,
                _ => StepScriptType,
            },
            _ => StepStart,
        };
        if (SetupStep == StepStart)
            RefreshSetupState();
    }

    [RelayCommand]
    private void CreateOrRestore()
    {
        string? password;
        if (EncryptWallet)
        {
            if (string.IsNullOrEmpty(PasswordInput))
            {
                StatusMessage = Loc.Tr("msg.password.required");
                return;
            }
            if (PasswordInput != ConfirmPasswordInput)
            {
                StatusMessage = Loc.Tr("msg.password.mismatch");
                return;
            }
            password = PasswordInput;
        }
        else
        {
            password = null;
        }

        try
        {
            WalletDocument doc;
            IWalletAccount account;

            switch (_wizardFlow)
            {
                case WizardFlowKind.ImportXkey:
                {
                    var input = ImportXkeyInput.Trim();
                    if (!TryReadImportOrigin(out var fingerprint, out var accountPath))
                        return;
                    if (Slip132.TryDecodePublic(input, Profile, out _, out _))
                    {
                        var (d, a) = WalletLoader.NewFromXpub(input, Profile, SelectedScriptKind, accountPath, fingerprint);
                        (doc, account) = (d, a);
                    }
                    else
                    {
                        var (d, a) = WalletLoader.NewFromXprv(input, Profile, SelectedScriptKind, accountPath, fingerprint);
                        (doc, account) = (d, a);
                    }
                    break;
                }
                case WizardFlowKind.Multisig:
                {
                    var (d, a) = NewMultisigFromInputs();
                    (doc, account) = (d, a);
                    break;
                }
                case WizardFlowKind.ImportWif:
                {
                    var wifLines = ImportWifInput.Split('\n',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var (d, a) = WalletLoader.NewFromWif(wifLines, SelectedScriptKind, Profile);
                    (doc, account) = (d, a);
                    break;
                }
                case WizardFlowKind.ImportAddress:
                {
                    var addressLines = ImportAddressInput.Split('\n',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var (d, a) = WalletLoader.NewFromAddresses(addressLines, Profile);
                    (doc, account) = (d, a);
                    break;
                }
                default:
                {
                    var (d, a) = WalletLoader.NewFromMnemonic(
                        MnemonicInput,
                        string.IsNullOrEmpty(PassphraseInput) ? null : PassphraseInput,
                        SelectedScriptKind,
                        Profile);
                    (doc, account) = (d, a);
                    break;
                }
            }

            var path = WalletPathFromName(WalletNameInput);
            if (WalletStore.Exists(path))
            {
                StatusMessage = Loc.Tr("msg.wallet.exists");
                return;
            }
            WalletStore.Save(doc, path, password);
            var newLock = WalletLock.TryAcquire(path);
            if (newLock is null) { StatusMessage = Loc.Tr("msg.wallet.locked"); return; }
            OpenLoaded(doc, account, path, password, newLock);
        }
        catch (Exception ex)
        {
            StatusMessage = $"{Loc.Tr("msg.error")}: {ex.Message}";
        }
    }

    /// <summary>
    /// Builds the file path from the name entered by the user.
    /// If the name is empty, generates an automatic name (default, wallet-2, …).
    /// Removes characters that are invalid for the filesystem.
    /// </summary>
    private string WalletPathFromName(string name)
    {
        var clean = string.IsNullOrWhiteSpace(name)
            ? ""
            : string.Concat(name.Trim()
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))
              .Trim('.');

        if (string.IsNullOrEmpty(clean))
        {
            // No name provided → auto-generated name
            var def = AppPaths.DefaultWalletPath(Net);
            for (var n = 2; WalletStore.Exists(def); n++)
                def = Path.Combine(AppPaths.WalletsDir(Net), $"wallet-{n}.wallet.json");
            return def;
        }

        return Path.Combine(AppPaths.WalletsDir(Net), $"{clean}.wallet.json");
    }

    [RelayCommand]
    private void OpenExisting()
    {
        var path = _pendingOpenPath ?? AppPaths.DefaultWalletPath(Net);
        var password = string.IsNullOrEmpty(PasswordInput) ? null : PasswordInput;

        var newLock = WalletLock.TryAcquire(path);
        if (newLock is null) { StatusMessage = Loc.Tr("msg.wallet.locked"); return; }

        try
        {
            var doc = WalletStore.Load(path, password);
            _pendingOpenPath = null;
            OpenLoaded(doc, WalletLoader.ToAccount(doc), path, password, newLock);
        }
        catch (WrongPasswordException)
        {
            newLock.Dispose();
            StatusMessage = Loc.Tr("msg.wrongpassword");
        }
        catch (UnauthorizedAccessException)
        {
            newLock.Dispose();
            StatusMessage = Loc.Tr("msg.wallet.noaccess");
        }
        catch (Exception ex)
        {
            newLock.Dispose();
            StatusMessage = $"{Loc.Tr("msg.error")}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void NewWallet()
    {
        if (IsWalletOpen)
            CloseWallet();
        _pendingOpenPath = null;
        StatusMessage = "";
    }

    private void OpenLoaded(
        WalletDocument doc, IWalletAccount account, string path, string? password, WalletLock walletLock)
    {
        _walletLock?.Dispose();
        _walletLock = walletLock;

        _doc = doc;
        _account = account;
        _walletPath = path;
        _password = password;
        MnemonicInput = ConfirmMnemonicInput = PassphraseInput = PasswordInput = ConfirmPasswordInput = WalletNameInput = "";
        ImportXkeyInput = ImportWifInput = ImportAddressInput = ImportXkeyDetectedKind = "";
        ImportFingerprintInput = ImportPathInput = "";
        ResetMultisigInputs();
        SetupStep = StepStart;
        OnPropertyChanged(nameof(IsWatchOnlyAccount));

        var walletKindTag = account switch
        {
            { IsWatchOnly: true } => " · watch-only",
            ImportedKeyAccount => " · imported",
            _ => ""
        };
        var pathTag = !string.IsNullOrEmpty(doc.AccountPath) ? $" · m/{doc.AccountPath}" : "";
        NetworkInfo = $"{doc.Network} · {doc.ScriptKind}{pathTag}{walletKindTag}";
        LoadContacts();
        ApplyCache(doc.Cache);
        IsWalletOpen = true;
        StatusMessage = Loc.Tr("msg.opened");
        _autoReconnect = true;  // keepalive retries the connection even if the first attempt fails
        _syncFailed = true;     // force the first automatic sync
        _ = ConnectAndSync();
    }
}