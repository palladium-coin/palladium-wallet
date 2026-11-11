using NBitcoin;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;
using PalladiumWallet.Tests.Crypto;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Tests.Net;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// Signing a PSBT made elsewhere from the GUI (ADR 0062): the second cosigner of a 2-of-3 wallet pastes or opens the
/// PSBT the first one prepared, reads what it does, signs it, and broadcasts it once the signatures are enough.
/// </summary>
[Collection("AppPaths")]
public sealed class PsbtGuiTests : VmTestBase
{
    private static readonly Network Net = PalladiumNetworks.Regtest;

    private async Task<MainWindowViewModel> OpenCosigner2()
    {
        MainWindowViewModel.ProfileOverride = ChainProfiles.Regtest with { BootstrapServers = [], Checkpoints = [] };
        var (doc, account) = WalletLoader.NewMultisig(2, MultisigAccountTests.AllKeys, MultisigAccountTests.Words2, null, ChainProfiles.Regtest);
        var path = AppPaths.DefaultWalletPath(NetKind.Regtest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WalletStore.Save(doc, path, null);
        WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        _server.Handle("server.features", _ => new { genesis_hash = ChainProfiles.Regtest.GenesisHash });
        var vm = new MainWindowViewModel();
        vm.WizardStartOpenCommand.Execute(null);
        vm.OpenExistingCommand.Execute(null);
        await Eventually(() => !vm.IsSyncing && vm.IsConnected, describe: () => $"status='{vm.StatusMessage}'");
        return vm;
    }

    /// <summary>The PSBT cosigner 1 hands over: built and signed by cosigner 1 for a spend of one coin of the wallet.</summary>
    private static (PSBT Psbt, string Txid) FromCosigner1()
    {
        var first = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var funding = Net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(100_000_000), first.GetReceiveAddress(0));
        var utxo = new CachedUtxo
        {
            Txid = funding.GetHash().ToString(), Vout = 0, ValueSats = 100_000_000, Address = first.GetReceiveAddress(0).ToString(),
            AddressIndex = 0, Height = 100, Verified = true,
        };
        var built = new TransactionFactory(first).Build([utxo], new Dictionary<string, Transaction> { [utxo.Txid] = funding },
            new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net), 30_000_000, 2, changeIndex: 0, tipHeight: 1_000);
        return (built.Psbt, built.Txid);
    }

    [Fact]
    public Task The_second_cosigner_reads_signs_and_broadcasts_the_psbt() => UiSession.Run(async () =>
    {
        var vm = await OpenCosigner2();
        var (psbt, txid) = FromCosigner1();
        string? sent = null;
        _server.Handle("blockchain.transaction.broadcast", p => { sent = p[0].GetString(); return Transaction.Parse(sent!, Net).GetHash().ToString(); });

        vm.OpenPsbtCommand.Execute(null);
        Assert.True(vm.IsPsbtOpen);
        vm.PsbtInput = psbt.ToBase64();
        vm.SignPsbtCommand.Execute(null);

        Assert.True(vm.HasFinalPsbt, vm.PsbtSummary);
        Assert.Contains(Loc.Tr("msg.psbt.complete"), vm.PsbtSummary);
        Assert.Contains(Loc.Tr("psbt.change"), vm.PsbtSummary);          // its own change is marked
        Assert.Contains(Loc.Tr("psbt.fee"), vm.PsbtSummary);
        Assert.Contains(Loc.Tr("psbt.signed"), vm.PsbtSummary);
        Assert.NotEqual("", vm.SignedPsbtBase64);

        await vm.BroadcastPsbtCommand.ExecuteAsync(null);

        Assert.StartsWith(Loc.Tr("msg.broadcasted"), vm.PsbtSummary);
        Assert.Contains(txid, vm.PsbtSummary);                            // a witness-only spend: the txid did not change
        Assert.Equal(txid, Transaction.Parse(sent!, Net).GetHash().ToString());
        Assert.False(vm.HasFinalPsbt);
    });

    [Fact]
    public Task A_psbt_file_in_binary_loads_and_the_signed_one_is_a_binary_file_again() => UiSession.Run(async () =>
    {
        var vm = await OpenCosigner2();
        var (psbt, _) = FromCosigner1();

        vm.LoadPsbtBytes(PsbtFormat.ToBinary(psbt));
        Assert.Equal(psbt.ToBase64(), vm.PsbtInput);
        Assert.Null(vm.SignedPsbtBytes());                                // nothing signed yet
        vm.SignPsbtCommand.Execute(null);

        var bytes = vm.SignedPsbtBytes();
        Assert.NotNull(bytes);
        Assert.Equal(vm.SignedPsbtBase64, PsbtFormat.Parse(bytes!, Net).ToBase64());
        Assert.True(PsbtFormat.Parse(bytes!, Net).Inputs[0].IsFinalized());           // the two signatures were enough
    });

    [Fact]
    public Task Text_that_is_no_psbt_and_a_junk_file_say_so_and_leave_nothing_to_save() => UiSession.Run(async () =>
    {
        var vm = await OpenCosigner2();
        vm.PsbtInput = "this is not a psbt";
        vm.SignPsbtCommand.Execute(null);
        Assert.Equal(Loc.Tr("msg.psbt.invalid"), vm.PsbtSummary);
        Assert.Null(vm.SignedPsbtBytes());
        Assert.False(vm.HasFinalPsbt);

        vm.LoadPsbtBytes([1, 2, 3]);
        Assert.Equal("", vm.PsbtInput);
        Assert.Equal(Loc.Tr("msg.psbt.invalid"), vm.PsbtSummary);
    });

    [Fact]
    public Task A_psbt_that_spends_nothing_of_this_wallet_is_refused_with_the_reason() => UiSession.Run(async () =>
    {
        var vm = await OpenCosigner2();
        var foreign = Net.CreateTransaction();
        foreign.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        foreign.Outputs.Add(Money.Satoshis(100_000), new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net));
        var psbt = PSBT.FromTransaction(foreign, Net);
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(110_000), new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net));

        vm.PsbtInput = psbt.ToBase64();
        vm.SignPsbtCommand.Execute(null);

        Assert.Contains("refusing to sign", vm.PsbtSummary);
        Assert.False(vm.HasFinalPsbt);
        Assert.Equal("", vm.SignedPsbtBase64);
    });

    [Fact]
    public Task Changing_the_text_discards_the_previous_result() => UiSession.Run(async () =>
    {
        var vm = await OpenCosigner2();
        var (psbt, _) = FromCosigner1();
        vm.PsbtInput = psbt.ToBase64();
        vm.SignPsbtCommand.Execute(null);
        Assert.True(vm.HasFinalPsbt);

        vm.PsbtInput += " ";                                              // any edit: the result is no longer this text's

        Assert.False(vm.HasFinalPsbt);
        Assert.Equal("", vm.SignedPsbtBase64);
        Assert.Null(vm.SignedPsbtBytes());
        await vm.BroadcastPsbtCommand.ExecuteAsync(null);                 // nothing to send
        Assert.Equal(0, _server.CallCount("blockchain.transaction.broadcast"));
    });

    [Fact]
    public Task An_incomplete_result_is_passed_on_and_cannot_be_broadcast() => UiSession.Run(async () =>
    {
        // Cosigner 1's own PSBT, signed by cosigner 1 again: still one signature of two.
        MainWindowViewModel.ProfileOverride = ChainProfiles.Regtest with { BootstrapServers = [], Checkpoints = [] };
        var (doc, account) = WalletLoader.NewMultisig(2, MultisigAccountTests.AllKeys, MultisigAccountTests.Words1, null, ChainProfiles.Regtest);
        var path = AppPaths.DefaultWalletPath(NetKind.Regtest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WalletStore.Save(doc, path, null);
        WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        _server.Handle("server.features", _ => new { genesis_hash = ChainProfiles.Regtest.GenesisHash });
        var vm = new MainWindowViewModel();
        vm.WizardStartOpenCommand.Execute(null);
        vm.OpenExistingCommand.Execute(null);
        await Eventually(() => !vm.IsSyncing && vm.IsConnected, describe: () => $"status='{vm.StatusMessage}'");

        vm.PsbtInput = FromCosigner1().Psbt.ToBase64();
        vm.SignPsbtCommand.Execute(null);

        Assert.False(vm.HasFinalPsbt);
        Assert.Contains(Loc.Tr("msg.psbt.incomplete"), vm.PsbtSummary);
        Assert.NotEqual("", vm.SignedPsbtBase64);                         // still handed on to the next cosigner
    });

    [Fact]
    public Task A_reply_with_another_txid_is_checked_against_the_server_like_a_send() => UiSession.Run(async () =>
    {
        var vm = await OpenCosigner2();
        var (psbt, txid) = FromCosigner1();
        vm.PsbtInput = psbt.ToBase64();
        vm.SignPsbtCommand.Execute(null);
        string? sent = null;
        _server.Handle("blockchain.transaction.broadcast", p => { sent = p[0].GetString(); return new string('a', 64); });
        _server.Handle("blockchain.transaction.get", p => p[0].GetString() == txid && sent is not null
            ? sent : throw new FakeElectrumError(2, "No such mempool or blockchain transaction"));

        await vm.BroadcastPsbtCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(Loc.Tr("msg.broadcast.innetwork"), txid), vm.PsbtSummary);
        Assert.False(vm.HasFinalPsbt);
    });

    [Fact]
    public Task A_multisig_wallet_exposes_its_configuration_and_the_overlay_closes() => UiSession.Run(async () =>
    {
        var vm = await OpenCosigner2();
        vm.OpenWalletInfoCommand.Execute(null);

        Assert.True(vm.WalletInfoIsMultisig);
        Assert.Contains("Policy: 2 of 3", vm.MultisigConfigText);
        Assert.Contains("sortedmulti(2,", vm.MultisigDescriptorsText);
        var lines = vm.MultisigDescriptorsText.Split('\n');
        Assert.Equal(2, lines.Length);                                    // receive and change, each with its checksum
        Assert.All(lines, l => Assert.Matches("#[a-z0-9]{8}$", l));
        Assert.EndsWith(MultisigAccountTests.Account().WalletId, lines[0]);

        vm.OpenPsbtCommand.Execute(null);
        vm.ClosePsbtCommand.Execute(null);
        Assert.False(vm.IsPsbtOpen);
    });
}
