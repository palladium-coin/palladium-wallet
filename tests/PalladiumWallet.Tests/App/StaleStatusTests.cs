using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;

namespace PalladiumWallet.Tests.App;

/// <summary>A status message is cleared once the thing it reported is over (no stale error on the status bar).</summary>
[Collection("AppPaths")]
public sealed class StaleStatusTests : VmTestBase
{
    [Fact]
    public Task A_seed_mismatch_message_does_not_survive_going_back() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.WizardStartNewCommand.Execute(null);
        vm.WizardNextFromShowSeedCommand.Execute(null);
        vm.ConfirmMnemonicInput = "wrong words";
        vm.WizardNextFromConfirmSeedCommand.Execute(null);
        Assert.Equal(Loc.Tr("msg.seed.mismatch"), vm.StatusMessage);

        vm.WizardBackCommand.Execute(null);

        Assert.True(vm.IsStepShowSeed);
        Assert.Equal("", vm.StatusMessage);
    });

    [Fact]
    public Task A_password_mismatch_message_does_not_survive_turning_encryption_off() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.WizardStartRestoreCommand.Execute(null);
        vm.MnemonicInput = Mnemonic;
        vm.WizardNextFromWordsCommand.Execute(null);
        vm.WizardNextFromPassphraseCommand.Execute(null);
        vm.WizardNextFromScriptTypeCommand.Execute(null);
        vm.PasswordInput = "abc";
        vm.ConfirmPasswordInput = "abd";
        vm.CreateOrRestoreCommand.Execute(null);
        Assert.Equal(Loc.Tr("msg.password.mismatch"), vm.StatusMessage);

        vm.EncryptWallet = false;

        Assert.Equal("", vm.StatusMessage);
    });

    [Fact]
    public Task An_invalid_contact_message_is_cleared_by_the_next_valid_contact() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 10_000_000);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "contacts");
        await Eventually(() => !vm.IsSyncing && vm.Coins.Any(c => c.IsSelectable));

        vm.NewContactName = "Bob";
        vm.NewContactAddress = "nonsense";
        vm.AddContactCommand.Execute(null);
        Assert.Equal(Loc.Tr("msg.contact.address.invalid"), vm.StatusMessage);

        vm.NewContactAddress = account.GetReceiveAddress(3).ToString();
        vm.AddContactCommand.Execute(null);

        Assert.Single(vm.Contacts);
        Assert.Equal("", vm.StatusMessage);
    });
}
