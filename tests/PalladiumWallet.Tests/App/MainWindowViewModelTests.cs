using Avalonia.Threading;
using NBitcoin;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;
using PalladiumWallet.Tests.Net;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// Headless tests of the single application ViewModel. Everything is sandboxed: the data
/// root is a temp folder, the saved server points at an in-process FakeElectrumServer (so the
/// auto-connect never reaches a real server) and the GitHub update check is stubbed.
/// </summary>
[Collection("AppPaths")]
public sealed class MainWindowViewModelTests : VmTestBase
{
    // ---- startup, overlays, settings ----

    [Fact]
    public Task Startup_ShowsTheStartStep_WithNoWalletOpen() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();

        Assert.True(vm.IsStepStart);
        Assert.False(vm.IsWalletOpen);
        Assert.True(vm.IsSetupVisible);
        Assert.StartsWith("Palladium Wallet", vm.WindowTitle);
        Assert.True(vm.IsDesktop);
    });

    [Fact]
    public Task SelectTab_ChangesTheSelectedIndex() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.SelectTabCommand.Execute("3");
        Assert.Equal(3, vm.SelectedTabIndex);
    });

    [Fact]
    public Task SettingsAndHelpOverlays_OpenAndClose() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.OpenSettingsCommand.Execute(null);
        Assert.True(vm.IsSettingsOpen);
        vm.CloseSettingsCommand.Execute(null);
        Assert.False(vm.IsSettingsOpen);
        vm.OpenHelpCommand.Execute(null);
        Assert.True(vm.IsHelpOpen);
        vm.CloseHelpCommand.Execute(null);
        Assert.False(vm.IsHelpOpen);
    });

    [Fact]
    public Task SetLanguage_SwitchesTheLocInstance_AndPersistsIt() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        var before = vm.Loc;

        vm.SetLanguageCommand.Execute("it");

        Assert.NotSame(before, vm.Loc);       // new instance → bindings re-evaluate
        Assert.Equal("it", vm.Loc.Language);
        Assert.Equal("it", AppConfig.Load(AppPaths.ConfigPath()).Language);
    });

    [Fact]
    public Task AvailableUpdate_OpensTheUpdateOverlay() => UiSession.Run(async () =>
    {
        MainWindowViewModel.UpdateCheck = _ => Task.FromResult<LatestRelease?>(new LatestRelease("v9.9.9", "https://example.test/r"));
        var vm = new MainWindowViewModel();

        await Eventually(() => vm.IsUpdateAvailableOpen);

        Assert.Equal("v9.9.9", vm.UpdateAvailableTag);
        Assert.Equal("https://example.test/r", vm.UpdateReleaseUrl);
        vm.CloseUpdateAvailableCommand.Execute(null);
        Assert.False(vm.IsUpdateAvailableOpen);
    });

    [Fact]
    public Task NoUpdate_LeavesTheOverlayClosed() => UiSession.Run(async () =>
    {
        var vm = new MainWindowViewModel();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsUpdateAvailableOpen);
    });

    // ---- wizard ----

    [Fact]
    public Task Restore_WithInvalidMnemonic_StaysOnTheWordsStep_WithAnError() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.WizardStartRestoreCommand.Execute(null);
        vm.MnemonicInput = "abandon abandon abandon";

        vm.WizardNextFromWordsCommand.Execute(null);

        Assert.True(vm.IsStepWords);
        Assert.Equal(Loc.Tr("msg.words.invalid"), vm.StatusMessage);
    });

    [Fact]
    public Task NewWallet_ConfirmingTheWrongSeed_IsRejected() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.WizardStartNewCommand.Execute(null);
        Assert.True(vm.IsStepShowSeed);
        Assert.True(Bip39.TryParse(vm.MnemonicInput, out _));
        vm.WizardNextFromShowSeedCommand.Execute(null);
        vm.ConfirmMnemonicInput = Mnemonic; // not the displayed one

        vm.WizardNextFromConfirmSeedCommand.Execute(null);

        Assert.True(vm.IsStepConfirmSeed);
        Assert.Equal(Loc.Tr("msg.seed.mismatch"), vm.StatusMessage);
    });

    [Fact]
    public Task NewWallet_ConfirmingTheRightSeed_AdvancesToThePassphraseStep() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.WizardStartNewCommand.Execute(null);
        vm.WizardNextFromShowSeedCommand.Execute(null);
        vm.ConfirmMnemonicInput = vm.MnemonicInput.ToUpperInvariant();

        vm.WizardNextFromConfirmSeedCommand.Execute(null);

        Assert.True(vm.IsStepPassphrase);
    });

    [Fact]
    public Task Password_Mismatch_OrEmpty_DoesNotCreateAnyFile() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.WizardStartRestoreCommand.Execute(null);
        vm.MnemonicInput = Mnemonic;
        vm.WizardNextFromWordsCommand.Execute(null);
        vm.WizardNextFromPassphraseCommand.Execute(null);
        vm.WizardNextFromScriptTypeCommand.Execute(null);

        vm.PasswordInput = vm.ConfirmPasswordInput = "";
        vm.CreateOrRestoreCommand.Execute(null);
        Assert.Equal(Loc.Tr("msg.password.required"), vm.StatusMessage);

        vm.PasswordInput = "one"; vm.ConfirmPasswordInput = "two";
        vm.CreateOrRestoreCommand.Execute(null);
        Assert.Equal(Loc.Tr("msg.password.mismatch"), vm.StatusMessage);

        Assert.False(vm.IsWalletOpen);
        Assert.Empty(AppPaths.WalletFiles(NetKind.Mainnet));
    });

    [Fact]
    public Task Restore_WithPassword_CreatesAnEncryptedWallet_AndOpensIt() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();

        RestoreFlow(vm, Mnemonic, "s3cret", "main");

        Assert.True(vm.IsWalletOpen);
        var file = Assert.Single(AppPaths.WalletFiles(NetKind.Mainnet));
        var content = File.ReadAllText(file);
        Assert.True(EncryptedFile.IsEncrypted(content));
        Assert.DoesNotContain("abandon", content);   // seed never in plaintext on disk
        Assert.Contains("NativeSegwit", vm.NetworkInfo);
    });

    [Fact]
    public Task Restore_WithoutEncryption_StoresPlainJson() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, password: null, name: "plain");

        Assert.True(vm.IsWalletOpen);
        Assert.False(EncryptedFile.IsEncrypted(File.ReadAllText(Assert.Single(AppPaths.WalletFiles(NetKind.Mainnet)))));
    });

    [Fact]
    public Task Restore_OverAnExistingName_DoesNotOverwriteTheWallet() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, "pw", "dup");
        var path = Assert.Single(AppPaths.WalletFiles(NetKind.Mainnet));
        var before = File.ReadAllBytes(path);
        vm.CloseWalletCommand.Execute(null);

        RestoreFlow(vm, Mnemonic, "other", "dup");

        Assert.Equal(Loc.Tr("msg.wallet.exists"), vm.StatusMessage);
        Assert.Equal(before, File.ReadAllBytes(path));
    });

    [Fact]
    public Task ImportAddress_CreatesAWatchOnlyWallet() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        var address = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet)
            .GetReceiveAddress(0).ToString();

        vm.WizardStartImportAddressCommand.Execute(null);
        vm.ImportAddressInput = address;
        vm.WizardNextFromImportAddressCommand.Execute(null);
        vm.EncryptWallet = false;
        vm.CreateOrRestoreCommand.Execute(null);

        Assert.True(vm.IsWalletOpen);
        Assert.True(vm.IsWatchOnlyAccount);
    });

    [Fact]
    public Task ImportXpub_CreatesAWatchOnlyWallet_ButANonsenseKeyIsRejected() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.WizardStartImportXkeyCommand.Execute(null);
        vm.ImportXkeyInput = "not-an-extended-key";
        vm.WizardNextFromImportXkeyCommand.Execute(null);
        Assert.True(vm.IsStepImportXkey);
        Assert.Equal(Loc.Tr("msg.xkey.invalid"), vm.StatusMessage);

        vm.ImportXkeyInput = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet).ToSlip132();
        vm.WizardNextFromImportXkeyCommand.Execute(null);
        vm.EncryptWallet = false;
        vm.CreateOrRestoreCommand.Execute(null);

        Assert.True(vm.IsWalletOpen);
        Assert.True(vm.IsWatchOnlyAccount);
    });


    // ---- open existing ----

    [Fact]
    public Task OpenExisting_WithWrongPassword_ShowsAnError_AndStaysClosed() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, "right", "w1");
        vm.CloseWalletCommand.Execute(null);

        vm.WizardStartOpenCommand.Execute(null);
        vm.PasswordInput = "wrong";
        vm.OpenExistingCommand.Execute(null);

        Assert.False(vm.IsWalletOpen);
        Assert.Equal(Loc.Tr("msg.wrongpassword"), vm.StatusMessage);
    });

    [Fact]
    public Task OpenExisting_WithTheRightPassword_OpensTheWallet() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, "right", "w1");
        vm.CloseWalletCommand.Execute(null);

        vm.WizardStartOpenCommand.Execute(null);
        vm.PasswordInput = "right";
        vm.OpenExistingCommand.Execute(null);

        Assert.True(vm.IsWalletOpen);
    });

    [Fact]
    public Task OpenExisting_WhileAnotherInstanceHoldsTheLock_IsRefused() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, "pw", "locked");
        var path = Assert.Single(AppPaths.WalletFiles(NetKind.Mainnet));
        // The first VM still holds the exclusive lock: a second instance must not open it.
        var second = new MainWindowViewModel();
        second.WizardStartOpenCommand.Execute(null);
        second.PasswordInput = "pw";
        second.OpenExistingCommand.Execute(null);

        Assert.False(second.IsWalletOpen);
        Assert.Equal(Loc.Tr("msg.wallet.locked"), second.StatusMessage);
        Assert.True(File.Exists(path));
    });

    // ---- contacts ----

    [Fact]
    public Task Contacts_AreAddedPersistedAndRemoved() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "c");

        var alice = Address(1);
        vm.NewContactName = "Alice"; vm.NewContactAddress = alice;
        vm.AddContactCommand.Execute(null);
        vm.NewContactName = ""; vm.NewContactAddress = "ignored";
        vm.AddContactCommand.Execute(null); // blank name → ignored

        Assert.Single(vm.Contacts);
        var stored = WalletStore.Load(Assert.Single(AppPaths.WalletFiles(NetKind.Mainnet)));
        Assert.Equal("Alice", Assert.Single(stored.Contacts).Name);

        vm.SelectedContactInList = vm.Contacts[0];
        vm.RemoveSelectedContactCommand.Execute(null);
        Assert.Empty(vm.Contacts);
        Assert.Empty(WalletStore.Load(Assert.Single(AppPaths.WalletFiles(NetKind.Mainnet))).Contacts);
    });

    private static string Address(int index) =>
        HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet)
            .GetReceiveAddress(index).ToString();

    [Theory]
    [InlineData("plm1qexample")]                                   // garbage
    [InlineData("not an address")]
    [InlineData("tplm1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx")]  // wrong network / bad checksum
    public Task Contacts_RejectInvalidAddresses(string bad) => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "c1");

        vm.NewContactName = "Bob"; vm.NewContactAddress = bad;
        vm.AddContactCommand.Execute(null);

        Assert.Empty(vm.Contacts);
        Assert.Equal(Loc.Tr("msg.contact.address.invalid"), vm.StatusMessage);
        Assert.Empty(WalletStore.Load(Assert.Single(AppPaths.WalletFiles(NetKind.Mainnet))).Contacts);
    });

    [Fact]
    public Task Contacts_RejectAnAddressFromAnotherNetwork() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "c2");
        var regtest = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Regtest)
            .GetReceiveAddress(0).ToString();

        vm.NewContactName = "Eve"; vm.NewContactAddress = regtest;
        vm.AddContactCommand.Execute(null);

        Assert.Empty(vm.Contacts);
        Assert.Equal(Loc.Tr("msg.contact.address.invalid"), vm.StatusMessage);
    });

    [Fact]
    public Task Contacts_RejectDuplicatesAndCleanPastedWhitespace() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "c3");
        var a = Address(2);

        vm.NewContactName = "  Carol "; vm.NewContactAddress = $" {a[..10]}\n{a[10..]}\r\n";
        vm.AddContactCommand.Execute(null);
        Assert.Equal(new ContactEntry("Carol", a), Assert.Single(vm.Contacts));

        vm.NewContactName = "Carol again"; vm.NewContactAddress = a.ToUpperInvariant();
        vm.AddContactCommand.Execute(null);
        Assert.Single(vm.Contacts);
        Assert.Equal(Loc.Tr("msg.contact.duplicate"), vm.StatusMessage);
    });

    [Fact]
    public Task Contacts_AlreadyStoredWithAnInvalidAddress_StillLoadAndDisplay() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "c4");
        var path = Assert.Single(AppPaths.WalletFiles(NetKind.Mainnet));
        var doc = WalletStore.Load(path);
        doc.Contacts = [new StoredContact { Name = "Legacy", Address = "whatever" }];
        WalletStore.Save(doc, path, null);
        vm.CloseWalletCommand.Execute(null);

        vm.WizardStartOpenCommand.Execute(null);
        vm.OpenExistingCommand.Execute(null);

        Assert.True(vm.IsWalletOpen);
        Assert.Equal(new ContactEntry("Legacy", "whatever"), Assert.Single(vm.Contacts));
    });

    // ---- sync + send against the fake server ----

    [Fact]
    public Task OpeningAFundedWallet_SyncsAgainstTheServer_AndShowsTheVerifiedHistory() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 2_500_000);
        var vm = new MainWindowViewModel();

        RestoreFlow(vm, Mnemonic, null, "funded");
        await Eventually(() => vm.IsConnected && !vm.IsSyncing && vm.History.Count == 1);

        Assert.True(vm.History[0].Verified);
        Assert.Contains("0.025", vm.BalanceText);
        Assert.NotEmpty(vm.Addresses);
        Assert.True(_server.CallCount("blockchain.transaction.get_merkle") >= 1); // Merkle proof was demanded
    });

    [Fact]
    public Task FeePresets_ComeFromTheServer_AndTheNormalEstimateIsPreselected() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        _server.Handle("blockchain.relayfee", _ => 0.00001m);
        _server.Handle("blockchain.estimatefee", p => p[0].GetInt32() switch
        {
            FeePolicy.FastTarget => 0.00005m,
            FeePolicy.NormalTarget => 0.00003m,
            _ => 0.00001m,
        });
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "fees");
        await Eventually(() => vm.HasFeeEstimates && !vm.IsSyncing);

        Assert.Equal("3", vm.SendFeeRate);                 // normal, preselected
        Assert.Contains("5", vm.FeeEstimateHint);
        vm.UseFeePresetCommand.Execute("fast");
        Assert.Equal("5", vm.SendFeeRate);
        vm.UseFeePresetCommand.Execute("economy");
        Assert.Equal("1", vm.SendFeeRate);
    });

    [Fact]
    public Task FeePresets_DoNotOverrideARateTheUserAlreadyTyped() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        _server.Handle("blockchain.relayfee", _ => 0.00001m);
        _server.Handle("blockchain.estimatefee", _ => 0.00004m);
        var vm = new MainWindowViewModel();
        vm.SendFeeRate = "7";                                 // typed before the estimates arrive
        RestoreFlow(vm, Mnemonic, null, "fees2");
        await Eventually(() => vm.HasFeeEstimates && !vm.IsSyncing);

        Assert.Equal("7", vm.SendFeeRate);
    });

    [Fact]
    public Task WithoutServerEstimates_TheManualDefaultStays_AndNoPresetsAreShown() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 50_000_000); // the fake answers "unknown method"
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "fees3");
        await Eventually(() => !vm.IsSyncing && vm.History.Count == 1);
        await Task.Delay(300); // let the optional estimate request settle

        Assert.False(vm.HasFeeEstimates);
        Assert.Equal("1", vm.SendFeeRate);
        vm.UseFeePresetCommand.Execute("fast");   // nothing to apply
        Assert.Equal("1", vm.SendFeeRate);
    });

    [Fact]
    public Task Send_RejectsARateBelowTheRelayFee_AndWarnsWhenTheFeeIsLarge() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        _server.Handle("blockchain.relayfee", _ => 0.00002m);          // 2 sat/vB
        _server.Handle("blockchain.estimatefee", _ => 0.00002m);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "fees4");
        await Eventually(() => vm.HasFeeEstimates && !vm.IsSyncing && vm.History.Count == 1);

        vm.SendTo = account.GetReceiveAddress(7).ToString();
        vm.SendAmount = "0.1";
        vm.SendFeeRate = "1";                                          // below the relay fee
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.False(vm.HasPendingSend);
        Assert.Equal(string.Format(Loc.Tr("msg.feerate.belowrelay"), "2"), vm.SendPreview);

        vm.SendFeeRate = "2";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingSend, vm.SendPreview);
        Assert.DoesNotContain(string.Format(Loc.Tr("msg.fee.high"), 1), vm.SendPreview);

        vm.SendAmount = "0.00001";                                      // 1,000 sat: the fee is >1%
        vm.SendFeeRate = "50";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingSend, vm.SendPreview);
        Assert.Contains(string.Format(Loc.Tr("msg.fee.high"), 1), vm.SendPreview);
    });

    [Fact]
    public Task Send_PreparesASignedTransaction_ForAFundedWallet_AndRejectsABadAmount() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "sender");
        await Eventually(() => !vm.IsSyncing && vm.History.Count == 1);
        // Coins need MinConfirmations (tip 200, height 100 → 101 confirmations): spendable.

        vm.SendTo = account.GetReceiveAddress(7).ToString();
        vm.SendAmount = "not-a-number";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Tr("msg.amount.invalid"), vm.SendPreview);
        Assert.False(vm.HasPendingSend);

        vm.SendAmount = "0.1";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingSend, vm.SendPreview);
        Assert.Contains("txid", vm.SendPreview);

        vm.SendAmount = "999";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.False(vm.HasPendingSend);
        Assert.Contains(Loc.Tr("msg.error"), vm.SendPreview);
    });

    [Fact]
    public Task Send_BeforeAnySync_AsksToSyncFirst() => UiSession.Run(async () =>
    {
        var vm = new MainWindowViewModel();
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Tr("msg.send.sync.first"), vm.SendPreview);
    });
}
