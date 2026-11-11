using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;

namespace PalladiumWallet.Tests.App;

/// <summary>The xpub import step accepts the key's origin (fingerprint and account path).</summary>
public sealed class WizardOriginTests : VmTestBase
{
    private static HdAccount Account()
    {
        Assert.True(Bip39.TryParse(Mnemonic, out var m));
        return HdAccount.FromMnemonic(m!, null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
    }

    private static WalletDocument OnlyWallet() =>
        WalletStore.Load(Assert.Single(AppPaths.WalletFiles(NetKind.Mainnet)));

    [Fact]
    public Task A_valid_fingerprint_and_path_are_stored_with_the_watch_only_wallet() => UiSession.Run(() =>
    {
        var account = Account();
        var vm = new MainWindowViewModel();
        vm.WizardStartImportXkeyCommand.Execute(null);
        vm.ImportXkeyInput = account.ToSlip132();
        vm.ImportFingerprintInput = " " + Convert.ToHexString(account.MasterFingerprint.ToBytes()) + " "; // upper case, padded
        vm.ImportPathInput = "m/84'/746'/0'";
        vm.WizardNextFromImportXkeyCommand.Execute(null);
        Assert.Equal("", vm.StatusMessage);
        vm.EncryptWallet = false;
        vm.CreateOrRestoreCommand.Execute(null);

        var doc = OnlyWallet();
        Assert.Equal(Convert.ToHexString(account.MasterFingerprint.ToBytes()).ToLowerInvariant(), doc.MasterFingerprint);
        Assert.Equal("84'/746'/0'", doc.AccountPath);
        Assert.True(((HdAccount)PalladiumWallet.Core.Wallet.WalletLoader.ToAccount(doc)).HasKnownOrigin);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Leaving_the_fields_empty_stores_no_fingerprint() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.WizardStartImportXkeyCommand.Execute(null);
        vm.ImportXkeyInput = Account().ToSlip132();
        vm.WizardNextFromImportXkeyCommand.Execute(null);
        vm.EncryptWallet = false;
        vm.CreateOrRestoreCommand.Execute(null);

        Assert.Null(OnlyWallet().MasterFingerprint);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("zz", "", "msg.xkey.fingerprint.invalid")]
    [InlineData("73c5da0", "", "msg.xkey.fingerprint.invalid")]
    [InlineData("73c5da0a1", "", "msg.xkey.fingerprint.invalid")]
    [InlineData("", "not/a/path", "msg.xkey.path.invalid")]
    [InlineData("73c5da0a", "m/84'/x/0'", "msg.xkey.path.invalid")]
    public Task A_malformed_fingerprint_or_path_blocks_the_step(string fingerprint, string path, string messageKey) =>
        UiSession.Run(() =>
        {
            var vm = new MainWindowViewModel();
            vm.WizardStartImportXkeyCommand.Execute(null);
            var step = vm.SetupStep;
            vm.ImportXkeyInput = Account().ToSlip132();
            vm.ImportFingerprintInput = fingerprint;
            vm.ImportPathInput = path;

            vm.WizardNextFromImportXkeyCommand.Execute(null);

            Assert.Equal(PalladiumWallet.App.Localization.Loc.Tr(messageKey), vm.StatusMessage);
            Assert.Equal(step, vm.SetupStep); // did not advance
            return Task.CompletedTask;
        });
}
