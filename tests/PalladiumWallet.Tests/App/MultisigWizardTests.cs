using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;
using PalladiumWallet.Tests.Crypto;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// Creating a multisig wallet from the GUI (ADR 0058): the cosigner keys, the wallet ID the cosigners compare, and the
/// refusal to write a wallet whose ID is not the one the others read.
/// </summary>
[Collection("AppPaths")]
public sealed class MultisigWizardTests : VmTestBase
{
    private static MainWindowViewModel Start()
    {
        MainWindowViewModel.ProfileOverride = ChainProfiles.Regtest with { BootstrapServers = [], Checkpoints = [] };
        var vm = new MainWindowViewModel();
        vm.WizardStartMultisigCommand.Execute(null);
        return vm;
    }

    private static string WalletFile => AppPaths.DefaultWalletPath(NetKind.Regtest);

    private static void Fill(MainWindowViewModel vm, string? own = MultisigAccountTests.Words1, IEnumerable<string>? keys = null)
    {
        vm.MultisigThresholdInput = "2";
        vm.MultisigKeysInput = string.Join("\n", keys ?? MultisigAccountTests.AllKeys);
        vm.MultisigMnemonicInput = own ?? "";
    }

    [Fact]
    public Task The_wizard_creates_a_multisig_wallet_after_the_id_matches() => UiSession.Run(async () =>
    {
        var vm = Start();
        Assert.True(vm.IsStepMultisig);
        Fill(vm);
        var expected = MultisigAccountTests.Account().WalletId;
        WireFundedChain(MultisigAccountTests.Account().GetReceiveAddress(0), 50_000_000);
        _server.Handle("server.features", _ => new { genesis_hash = ChainProfiles.Regtest.GenesisHash });

        vm.WizardNextFromMultisigCommand.Execute(null);
        Assert.True(vm.IsStepMultisigVerify, vm.StatusMessage);
        Assert.Equal(expected, vm.MultisigWalletId);

        vm.MultisigExpectedIdInput = expected.ToUpperInvariant();       // typed as the others read it, in any case
        vm.WizardNextFromMultisigVerifyCommand.Execute(null);
        Assert.True(vm.IsStepPassword);
        Assert.Equal("", vm.StatusMessage);

        vm.EncryptWallet = false;
        vm.CreateOrRestoreCommand.Execute(null);

        Assert.True(vm.IsWalletOpen, vm.StatusMessage);
        var doc = WalletStore.Load(WalletFile, null);
        var account = Assert.IsType<MultisigAccount>(WalletLoader.ToAccount(doc));
        Assert.Equal(expected, account.WalletId);
        Assert.False(account.IsWatchOnly);                              // the seed was given: it signs its share
        Assert.Equal(2, account.Threshold);
        await Eventually(() => !vm.IsSyncing && vm.IsConnected && vm.Coins.Count == 1,
            describe: () => $"syncing={vm.IsSyncing} coins={vm.Coins.Count} status='{vm.StatusMessage}'");
    });

    [Fact]
    public Task A_wallet_id_that_is_not_the_one_the_others_read_writes_nothing() => UiSession.Run(async () =>
    {
        var vm = Start();
        Fill(vm);
        vm.WizardNextFromMultisigCommand.Execute(null);
        vm.MultisigExpectedIdInput = "abcd2345";

        vm.WizardNextFromMultisigVerifyCommand.Execute(null);

        Assert.True(vm.IsStepMultisigVerify);                            // stays here
        Assert.Contains(vm.MultisigWalletId, vm.StatusMessage);
        Assert.Contains("abcd2345", vm.StatusMessage);
        Assert.False(File.Exists(WalletFile));
        await Task.CompletedTask;
    });

    [Fact]
    public Task Without_an_id_to_compare_the_wallet_is_created_but_flagged_as_not_verified() => UiSession.Run(async () =>
    {
        var vm = Start();
        Fill(vm, own: null);
        vm.WizardNextFromMultisigCommand.Execute(null);

        vm.WizardNextFromMultisigVerifyCommand.Execute(null);

        Assert.True(vm.IsStepPassword);
        Assert.Equal(Loc.Tr("msg.multisig.unverified"), vm.StatusMessage);
        vm.EncryptWallet = false;
        vm.CreateOrRestoreCommand.Execute(null);
        var doc = WalletStore.Load(WalletFile, null);
        Assert.True(WalletLoader.ToAccount(doc).IsWatchOnly);            // no seed: it coordinates, it cannot sign
        await Task.CompletedTask;
    });

    [Fact]
    public Task Wrapped_is_a_wallet_of_its_own_with_its_own_keys() => UiSession.Run(async () =>
    {
        var vm = Start();
        vm.MultisigMnemonicInput = MultisigAccountTests.Words1;
        vm.ShowMultisigOwnKeyCommand.Execute(null);
        Assert.StartsWith("[73c5da0a/48'/1'/0'/2']Vpub", vm.MultisigOwnKey);

        vm.MultisigWrapped = true;
        Assert.Equal("", vm.MultisigOwnKey);                            // a key shown for the other type no longer applies
        vm.ShowMultisigOwnKeyCommand.Execute(null);
        Assert.StartsWith("[73c5da0a/48'/1'/0'/1']Upub", vm.MultisigOwnKey);

        var keys = new[] { MultisigAccountTests.Words1, MultisigAccountTests.Words2, MultisigAccountTests.Words3 }
            .Select(w => Cosigner.FromMnemonic(w, null, ScriptKind.WrappedSegwitMultisig, ChainProfiles.Regtest)
                .ToText(ScriptKind.WrappedSegwitMultisig, ChainProfiles.Regtest));
        Fill(vm, keys: keys);
        vm.WizardNextFromMultisigCommand.Execute(null);
        Assert.Equal("ryrrzpe2", vm.MultisigWalletId);                   // the ID Palladium Core computes for it
        vm.WizardNextFromMultisigVerifyCommand.Execute(null);
        vm.EncryptWallet = false;
        vm.CreateOrRestoreCommand.Execute(null);

        var account = Assert.IsType<MultisigAccount>(WalletLoader.ToAccount(WalletStore.Load(WalletFile, null)));
        Assert.True(account.IsWrapped);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData("x", "msg.multisig.threshold")]
    [InlineData("4", null)]                                              // out of range: the wallet's own message
    public Task A_bad_threshold_stays_on_the_step_and_says_why(string threshold, string? key) => UiSession.Run(async () =>
    {
        var vm = Start();
        Fill(vm);
        vm.MultisigThresholdInput = threshold;

        vm.WizardNextFromMultisigCommand.Execute(null);

        Assert.True(vm.IsStepMultisig);
        Assert.NotEqual("", vm.StatusMessage);
        if (key is not null) Assert.Equal(Loc.Tr(key), vm.StatusMessage);
        else Assert.Contains("between 1 and 3", vm.StatusMessage);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Bad_keys_and_a_seed_that_matches_none_are_refused_with_a_message() => UiSession.Run(async () =>
    {
        var vm = Start();
        Fill(vm, keys: [MultisigAccountTests.KeyOf(MultisigAccountTests.Words1), "not a key"]);
        vm.WizardNextFromMultisigCommand.Execute(null);
        Assert.True(vm.IsStepMultisig);
        Assert.Contains("not a cosigner key", vm.StatusMessage);

        // A well-formed set of keys, none of them from the seed typed in.
        Fill(vm, own: "legal winner thank year wave sausage worth useful legal winner thank yellow",
            keys: [MultisigAccountTests.KeyOf(MultisigAccountTests.Words1), MultisigAccountTests.KeyOf(MultisigAccountTests.Words2)]);
        vm.WizardNextFromMultisigCommand.Execute(null);
        Assert.True(vm.IsStepMultisig);
        Assert.Contains("None of the cosigner keys comes from this seed", vm.StatusMessage);

        vm.MultisigMnemonicInput = "not a seed";
        vm.ShowMultisigOwnKeyCommand.Execute(null);
        Assert.Equal("", vm.MultisigOwnKey);
        Assert.Equal(Loc.Tr("msg.words.invalid"), vm.StatusMessage);
        await Task.CompletedTask;
    });

    [Fact]
    public Task A_generated_seed_is_a_valid_one_and_back_returns_to_the_start() => UiSession.Run(async () =>
    {
        var vm = Start();
        vm.GenerateMultisigSeedCommand.Execute(null);
        Assert.True(Bip39.TryParse(vm.MultisigMnemonicInput, out _));

        Fill(vm, own: vm.MultisigMnemonicInput);
        vm.WizardBackCommand.Execute(null);
        Assert.True(vm.IsStepStart);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Back_from_the_password_step_returns_to_the_id_check() => UiSession.Run(async () =>
    {
        var vm = Start();
        Fill(vm);
        vm.WizardNextFromMultisigCommand.Execute(null);
        vm.WizardNextFromMultisigVerifyCommand.Execute(null);
        Assert.True(vm.IsStepPassword);

        vm.WizardBackCommand.Execute(null);
        Assert.True(vm.IsStepMultisigVerify);
        vm.WizardBackCommand.Execute(null);
        Assert.True(vm.IsStepMultisig);
        await Task.CompletedTask;
    });
}
