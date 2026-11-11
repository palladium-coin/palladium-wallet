using NBitcoin;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Tests.Net;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// A broadcast whose reply names another txid is not trusted either way: the server is asked for the
/// transaction we built, and that answer decides whether the send is done or stays pending.
/// </summary>
[Collection("AppPaths")]
public sealed class BroadcastReconciliationTests : VmTestBase
{
    private static readonly Network Net = PalladiumNetworks.Mainnet;
    private static readonly string WrongTxid = new('b', 64);

    private async Task<(MainWindowViewModel Vm, string FundingTxid, string FundingHex)> PreparedSend()
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        var (funding, fundingHex) = WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "recon");
        await Eventually(() => !vm.IsSyncing && vm.History.Count == 1 && vm.History[0].Verified,
            describe: () => $"syncing={vm.IsSyncing} rows={vm.History.Count} status='{vm.StatusMessage}'");
        vm.SendTo = account.GetReceiveAddress(7).ToString();
        vm.SendAmount = "0.1";
        await vm.PrepareSendCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingSend, vm.SendPreview);
        return (vm, funding.GetHash().ToString(), fundingHex);
    }

    [Fact]
    public Task WhenTheServerHoldsTheTransaction_TheSendIsDone_AndTheAnomalyIsReported() => UiSession.Run(async () =>
    {
        var (vm, fundingTxid, fundingHex) = await PreparedSend();
        string? sent = null;
        _server.Handle("blockchain.transaction.broadcast", p => { sent = p[0].GetString(); return WrongTxid; });
        _server.Handle("blockchain.transaction.get", p =>
            sent is not null && Transaction.Parse(sent, Net).GetHash().ToString() == p[0].GetString() ? sent
            : p[0].GetString() == fundingTxid ? fundingHex
            : throw new FakeElectrumError(2, "No such mempool or blockchain transaction"));

        await vm.ConfirmSendCommand.ExecuteAsync(null);

        var ourTxid = Transaction.Parse(sent!, Net).GetHash().ToString();
        Assert.Equal(string.Format(Loc.Tr("msg.broadcast.innetwork"), ourTxid), vm.SendPreview);
        Assert.False(vm.HasPendingSend);
        Assert.Equal("", vm.SendTo);
    });

    [Fact]
    public Task WhenTheServerDoesNotHoldTheTransaction_ItStaysPending_AndCanBeSentAgain() => UiSession.Run(async () =>
    {
        var (vm, fundingTxid, fundingHex) = await PreparedSend();
        _server.Handle("blockchain.transaction.broadcast", _ => WrongTxid);
        _server.Handle("blockchain.transaction.get", p =>
            p[0].GetString() == fundingTxid ? fundingHex
            : throw new FakeElectrumError(2, "No such mempool or blockchain transaction"));

        await vm.ConfirmSendCommand.ExecuteAsync(null);

        Assert.Equal(Loc.Tr("msg.broadcast.notinnetwork"), vm.SendPreview);
        Assert.True(vm.HasPendingSend);
        Assert.NotEqual("", vm.SendTo);

        await vm.ConfirmSendCommand.ExecuteAsync(null);                 // the same signed bytes, sent again
        Assert.Equal(2, _server.CallCount("blockchain.transaction.broadcast"));
        Assert.True(vm.HasPendingSend);
    });

    [Fact]
    public Task WhenTheCheckCannotBeCompleted_TheTransactionStaysPending() => UiSession.Run(async () =>
    {
        var (vm, fundingTxid, fundingHex) = await PreparedSend();
        _server.Handle("blockchain.transaction.broadcast", _ => WrongTxid);
        _server.Handle("blockchain.transaction.get", p =>
        {
            if (p[0].GetString() == fundingTxid)
                return fundingHex;
            _server.DropClients(); // the connection dies while the check is being made
            return null;
        });

        await vm.ConfirmSendCommand.ExecuteAsync(null);

        Assert.Equal(Loc.Tr("msg.broadcast.checkfailed"), vm.SendPreview);
        Assert.True(vm.HasPendingSend);
    });

    [Fact]
    public Task AMatchingReply_StillCompletesTheSendAsBefore() => UiSession.Run(async () =>
    {
        var (vm, _, _) = await PreparedSend();
        string? sent = null;
        _server.Handle("blockchain.transaction.broadcast", p =>
        {
            sent = p[0].GetString();
            return Transaction.Parse(sent!, Net).GetHash().ToString();
        });

        await vm.ConfirmSendCommand.ExecuteAsync(null);

        Assert.StartsWith(Loc.Tr("msg.broadcasted"), vm.SendPreview);
        Assert.False(vm.HasPendingSend);
    });

    // ----- the Donate tab shares ReconcileBroadcastMismatchAsync, but has its own flow around it -----

    private async Task<(MainWindowViewModel Vm, string FundingTxid, string FundingHex)> PreparedDonation()
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        var (funding, fundingHex) = WireFundedChain(account.GetReceiveAddress(0), 50_000_000);
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "reconDonate");
        await Eventually(() => !vm.IsSyncing && vm.History.Count == 1 && vm.History[0].Verified,
            describe: () => $"syncing={vm.IsSyncing} rows={vm.History.Count} status='{vm.StatusMessage}'");
        vm.DonateAmount = "0.01";
        await vm.PrepareDonateCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingDonate, vm.DonatePreview);
        return (vm, funding.GetHash().ToString(), fundingHex);
    }

    [Fact]
    public Task Donate_WhenTheServerHoldsTheTransaction_TheDonationIsDone_AndTheAnomalyIsReported() => UiSession.Run(async () =>
    {
        var (vm, fundingTxid, fundingHex) = await PreparedDonation();
        string? sent = null;
        _server.Handle("blockchain.transaction.broadcast", p => { sent = p[0].GetString(); return WrongTxid; });
        _server.Handle("blockchain.transaction.get", p =>
            sent is not null && Transaction.Parse(sent, Net).GetHash().ToString() == p[0].GetString() ? sent
            : p[0].GetString() == fundingTxid ? fundingHex
            : throw new FakeElectrumError(2, "No such mempool or blockchain transaction"));

        await vm.ConfirmDonateCommand.ExecuteAsync(null);

        var ourTxid = Transaction.Parse(sent!, Net).GetHash().ToString();
        Assert.Equal(string.Format(Loc.Tr("msg.broadcast.innetwork"), ourTxid), vm.DonatePreview);
        Assert.False(vm.HasPendingDonate);
        Assert.Equal("", vm.DonateAmount);                              // finished: the form is cleared
        Assert.Contains(Transaction.Parse(sent!, Net).Outputs,
            o => o.ScriptPubKey == BitcoinAddress.Create(MainWindowViewModel.DevAddress, Net).ScriptPubKey);
    });

    [Fact]
    public Task Donate_WhenTheServerDoesNotHoldTheTransaction_ItStaysPending_AndCanBeSentAgain() => UiSession.Run(async () =>
    {
        var (vm, fundingTxid, fundingHex) = await PreparedDonation();
        _server.Handle("blockchain.transaction.broadcast", _ => WrongTxid);
        _server.Handle("blockchain.transaction.get", p =>
            p[0].GetString() == fundingTxid ? fundingHex
            : throw new FakeElectrumError(2, "No such mempool or blockchain transaction"));

        await vm.ConfirmDonateCommand.ExecuteAsync(null);

        Assert.Equal(Loc.Tr("msg.broadcast.notinnetwork"), vm.DonatePreview);
        Assert.True(vm.HasPendingDonate);
        Assert.NotEqual("", vm.DonateAmount);

        await vm.ConfirmDonateCommand.ExecuteAsync(null);               // the same signed bytes, sent again
        Assert.Equal(2, _server.CallCount("blockchain.transaction.broadcast"));
        Assert.True(vm.HasPendingDonate);
    });

    [Fact]
    public Task Donate_WhenTheCheckCannotBeCompleted_TheDonationStaysPending() => UiSession.Run(async () =>
    {
        var (vm, fundingTxid, fundingHex) = await PreparedDonation();
        _server.Handle("blockchain.transaction.broadcast", _ => WrongTxid);
        _server.Handle("blockchain.transaction.get", p =>
        {
            if (p[0].GetString() == fundingTxid)
                return fundingHex;
            _server.DropClients();
            return null;
        });

        await vm.ConfirmDonateCommand.ExecuteAsync(null);

        Assert.Equal(Loc.Tr("msg.broadcast.checkfailed"), vm.DonatePreview);
        Assert.True(vm.HasPendingDonate);
    });
}
