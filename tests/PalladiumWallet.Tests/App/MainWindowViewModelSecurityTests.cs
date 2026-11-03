using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// Secret-handling gates in the UI layer: the seed and private keys must stay hidden until the
/// wallet password is supplied, and must be hidden again on close. Also settings, wallet info
/// and the remaining wizard flows.
/// </summary>
[Collection("AppPaths")]
public sealed class MainWindowViewModelSecurityTests : VmTestBase
{
    [Fact]
    public Task Seed_StaysHidden_UntilTheRightPasswordIsGiven() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, "pw", "s1");
        vm.OpenWalletInfoCommand.Execute(null);
        Assert.True(vm.WalletInfoHasSeed);
        Assert.True(vm.WalletInfoSeedNeedsPassword);
        Assert.Equal("", vm.WalletInfoSeedText);

        vm.WalletInfoSeedPasswordInput = "nope";
        vm.RevealSeedCommand.Execute(null);
        Assert.False(vm.IsWalletInfoSeedRevealed);
        Assert.Equal(Loc.Tr("msg.wrongpassword"), vm.WalletInfoSeedError);
        Assert.Equal("", vm.WalletInfoSeedText);

        vm.WalletInfoSeedPasswordInput = "pw";
        vm.RevealSeedCommand.Execute(null);
        Assert.True(vm.IsWalletInfoSeedRevealed);
        Assert.Equal(Mnemonic, vm.WalletInfoSeedText);

        vm.CloseWalletInfoCommand.Execute(null);        // closing wipes the revealed state
        Assert.False(vm.IsWalletInfoSeedRevealed);
        Assert.Equal("", vm.WalletInfoSeedText);
    });

    [Fact]
    public Task WatchOnlyWallet_HasNoSeedToReveal() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        var address = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet)
            .GetReceiveAddress(0).ToString();
        vm.WizardStartImportAddressCommand.Execute(null);
        vm.ImportAddressInput = address;
        vm.WizardNextFromImportAddressCommand.Execute(null);
        vm.EncryptWallet = false;
        vm.CreateOrRestoreCommand.Execute(null);
        vm.OpenWalletInfoCommand.Execute(null);

        Assert.False(vm.WalletInfoHasSeed);
        vm.RevealSeedCommand.Execute(null);
        Assert.False(vm.IsWalletInfoSeedRevealed);
    });

    [Fact]
    public Task PrivateKey_IsRevealedOnlyAfterThePassword_AndOnlyWhenTheWalletHasKeys() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 1_000_000);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, "pw", "pk");
        await Eventually(() => !vm.IsSyncing && vm.Addresses.Count > 0);

        var row = vm.Addresses.First(a => !a.IsChange && a.Indice == 0);
        Assert.True(row.HasPrivKey);
        var wif = account.GetPrivateKey(false, 0)!.GetWif(PalladiumNetworks.For(NetKind.Mainnet)).ToString();

        // No row holds a private key before the reveal (reflection over every string property).
        Assert.All(vm.Addresses, r => Assert.DoesNotContain(wif, StringValues(r)));
        Assert.DoesNotContain(typeof(AddressRow).GetProperties(), p => p.Name.Contains("PrivKey") && p.PropertyType == typeof(string));

        vm.ShowAddressInfo(row);
        Assert.DoesNotContain(wif, StringValues(vm.AddressInfo!));
        Assert.Equal("", vm.RevealedPrivKey);

        vm.RequestPrivKeyRevealCommand.Execute(null);
        Assert.True(vm.IsPrivKeyPromptOpen);
        Assert.False(vm.IsAddressPrivKeyRevealed);
        Assert.Equal("", vm.RevealedPrivKey);

        vm.PrivKeyPromptPassword = "wrong";
        vm.ConfirmPrivKeyPasswordCommand.Execute(null);
        Assert.False(vm.IsAddressPrivKeyRevealed);
        Assert.Equal(Loc.Tr("msg.wrongpassword"), vm.PrivKeyPromptError);

        vm.PrivKeyPromptPassword = "pw";
        vm.ConfirmPrivKeyPasswordCommand.Execute(null);
        Assert.True(vm.IsAddressPrivKeyRevealed);
        Assert.False(vm.IsPrivKeyPromptOpen);
        Assert.Equal(wif, vm.RevealedPrivKey);
        Assert.DoesNotContain(wif, StringValues(vm.AddressInfo!)); // still not stored in the info
        Assert.All(vm.Addresses, r => Assert.DoesNotContain(wif, StringValues(r)));

        vm.HideAddressPrivKeyCommand.Execute(null);   // hiding drops the key
        Assert.False(vm.IsAddressPrivKeyRevealed);
        Assert.Equal("", vm.RevealedPrivKey);

        vm.RequestPrivKeyRevealCommand.Execute(null); // reveal again, then close the sheet
        vm.PrivKeyPromptPassword = "pw";
        vm.ConfirmPrivKeyPasswordCommand.Execute(null);
        Assert.Equal(wif, vm.RevealedPrivKey);
        vm.CloseAddressInfoCommand.Execute(null);     // closing the sheet re-hides everything
        Assert.Equal("", vm.RevealedPrivKey);
        vm.ShowAddressInfo(row);
        Assert.False(vm.IsAddressPrivKeyRevealed);
    });

    [Fact]
    public Task ClosingTheWallet_DropsARevealedPrivateKey() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 1_000_000);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "pk2");
        await Eventually(() => !vm.IsSyncing && vm.Addresses.Count > 0);

        vm.ShowAddressInfo(vm.Addresses.First(a => !a.IsChange && a.Indice == 0));
        vm.RequestPrivKeyRevealCommand.Execute(null);   // no password: revealed directly
        Assert.NotEqual("", vm.RevealedPrivKey);

        vm.CloseWalletCommand.Execute(null);
        Assert.Equal("", vm.RevealedPrivKey);
        Assert.False(vm.IsAddressPrivKeyRevealed);
        Assert.Null(vm.AddressInfo);
    });

    private static IEnumerable<string> StringValues(object o) =>
        o.GetType().GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
            .Select(p => (string?)p.GetValue(o) ?? "");

    [Fact]
    public Task WatchOnlyAddressRows_NeverCarryAPrivateKey() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 1_000_000);
        var vm = new MainWindowViewModel();
        vm.WizardStartImportXkeyCommand.Execute(null);
        vm.ImportXkeyInput = account.ToSlip132();
        vm.WizardNextFromImportXkeyCommand.Execute(null);
        vm.EncryptWallet = false;
        vm.CreateOrRestoreCommand.Execute(null);
        await Eventually(() => !vm.IsSyncing && vm.Addresses.Count > 0);

        Assert.All(vm.Addresses, a => Assert.False(a.HasPrivKey));
        vm.ShowAddressInfo(vm.Addresses[0]);
        vm.RequestPrivKeyRevealCommand.Execute(null);
        Assert.False(vm.IsAddressPrivKeyRevealed);
        Assert.False(vm.IsPrivKeyPromptOpen);
    });

    [Fact]
    public Task Units_AndLanguages_AreAppliedAndPersisted() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();

        vm.SetUnitCommand.Execute("sat");
        Assert.True(vm.IsUnitSat);
        Assert.Equal("sat", vm.UnitLabel);
        Assert.Equal("sat", AppConfig.Load(AppPaths.ConfigPath()).Unit);

        foreach (var lang in Loc.Languages)
        {
            vm.SetLanguageCommand.Execute(lang);
            Assert.Equal(lang, vm.Loc.Language);
        }
        Assert.True(vm.IsLangZh);
        Assert.False(vm.IsLangIt);
    });

    [Fact]
    public Task SettingsFromDisk_AreRestoredByANewViewModel() => UiSession.Run(() =>
    {
        new MainWindowViewModel().SetLanguageCommand.Execute("de");

        var vm2 = new MainWindowViewModel();

        Assert.Equal("de", vm2.Loc.Language);
        Assert.True(vm2.IsLangDe);
    });

    [Fact]
    public Task Wizard_BackNavigation_ReturnsToThePreviousStep() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.WizardStartRestoreCommand.Execute(null);
        vm.MnemonicInput = Mnemonic;
        vm.WizardNextFromWordsCommand.Execute(null);
        Assert.True(vm.IsStepPassphrase);

        vm.WizardBackCommand.Execute(null);
        Assert.True(vm.IsStepWords);
        vm.WizardBackCommand.Execute(null);
        Assert.True(vm.IsStepStart);
    });

    [Fact]
    public Task ImportWif_CreatesAnImportedKeyWallet_AndRejectsGarbage() => UiSession.Run(() =>
    {
        var key = new NBitcoin.Key();
        var wif = key.GetWif(PalladiumNetworks.Mainnet).ToString();
        var vm = new MainWindowViewModel();

        vm.WizardStartImportWifCommand.Execute(null);
        vm.ImportWifInput = "definitely-not-a-wif";
        vm.WizardNextFromImportWifCommand.Execute(null);
        Assert.True(vm.IsStepImportWif);

        vm.ImportWifInput = wif;
        vm.WizardNextFromImportWifCommand.Execute(null);
        vm.EncryptWallet = true;
        vm.PasswordInput = vm.ConfirmPasswordInput = "pw";
        vm.CreateOrRestoreCommand.Execute(null);

        Assert.True(vm.IsWalletOpen);
        Assert.Contains("imported", vm.NetworkInfo);
        var stored = WalletStore.Load(Assert.Single(AppPaths.WalletFiles(NetKind.Mainnet)), "pw");
        Assert.Equal(wif, Assert.Single(stored.WifKeys!));
    });

    [Fact]
    public Task ImportXprv_CreatesASpendableWallet() => UiSession.Run(() =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        var vm = new MainWindowViewModel();
        vm.WizardStartImportXkeyCommand.Execute(null);
        vm.ImportXkeyInput = account.ToSlip132Private();
        vm.WizardNextFromImportXkeyCommand.Execute(null);
        vm.EncryptWallet = false;
        vm.CreateOrRestoreCommand.Execute(null);

        Assert.True(vm.IsWalletOpen);
        Assert.False(vm.IsWatchOnlyAccount);
    });

    [Fact]
    public Task ClosingTheWallet_ReleasesTheLock_SoItCanBeReopened() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, "pw", "relock");
        var path = Assert.Single(AppPaths.WalletFiles(NetKind.Mainnet));
        Assert.Null(WalletLock.TryAcquire(path));      // held while open

        vm.CloseWalletCommand.Execute(null);

        using var l = WalletLock.TryAcquire(path);      // released on close
        Assert.NotNull(l);
        Assert.False(vm.IsWalletOpen);
    });
}
