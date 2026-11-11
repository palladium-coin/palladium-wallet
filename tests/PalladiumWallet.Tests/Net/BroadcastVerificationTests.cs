using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Net;

namespace PalladiumWallet.Tests.Net;

/// <summary>
/// The broadcast reply must name the txid computed locally; any other reply is reported as a
/// mismatch and never as a successful send. Used by the GUI and by the CLI.
/// </summary>
public class BroadcastVerificationTests
{
    private const string Sent = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ReplyWithAnotherTxid_IsAMismatch_NotASuccess()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.transaction.broadcast", _ => new string('b', 64));
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var ex = await Assert.ThrowsAsync<BroadcastTxidMismatchException>(
            () => client.BroadcastVerifiedAsync("beef", Sent));

        Assert.Equal(Sent, ex.ExpectedTxid);
        Assert.Equal(new string('b', 64), ex.ReportedTxid);
    }

    [Fact]
    public async Task ReplyWithTheSameTxid_IsAccepted_WhateverTheCase()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.transaction.broadcast", _ => Sent.ToUpperInvariant());
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var txid = await client.BroadcastVerifiedAsync("beef", Sent);

        Assert.Equal(Sent.ToUpperInvariant(), txid);
    }

    [Fact]
    public async Task ServerError_IsNotReportedAsAMismatch()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.transaction.broadcast",
            _ => throw new FakeElectrumError(1, "bad-txns-inputs-missingorspent"));
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var ex = await Assert.ThrowsAsync<ElectrumServerException>(
            () => client.BroadcastVerifiedAsync("beef", Sent));
        Assert.Contains("missingorspent", ex.Message);
    }

    // ---- HasTransactionAsync: the transaction the server holds is the evidence, not its reply ----

    private static readonly Network Net = PalladiumNetworks.Regtest;

    private static Transaction SampleTx(long sats)
    {
        var tx = Net.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(Money.Satoshis(sats), new Key().PubKey.WitHash.GetAddress(Net));
        return tx;
    }

    [Fact]
    public async Task HasTransaction_IsTrue_WhenTheServerReturnsTheTransactionWithThatHash()
    {
        var tx = SampleTx(1_000);
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.transaction.get", _ => tx.ToHex());
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        Assert.True(await client.HasTransactionAsync(tx.GetHash().ToString(), Net));
    }

    [Fact]
    public async Task HasTransaction_IsFalse_WhenTheServerDoesNotKnowTheTransaction()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.transaction.get",
            _ => throw new FakeElectrumError(2, "No such mempool or blockchain transaction"));
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        Assert.False(await client.HasTransactionAsync(Sent, Net));
    }

    [Fact]
    public async Task HasTransaction_IsFalse_WhenTheServerReturnsAnotherTransaction()
    {
        var asked = SampleTx(1_000);
        var other = SampleTx(2_000);
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.transaction.get", _ => other.ToHex());
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        Assert.False(await client.HasTransactionAsync(asked.GetHash().ToString(), Net));
    }

    [Fact]
    public async Task HasTransaction_IsFalse_WhenTheServerReturnsSomethingThatIsNotATransaction()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.transaction.get", _ => "zz-not-hex");
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        Assert.False(await client.HasTransactionAsync(Sent, Net));
    }

    [Fact]
    public async Task HasTransaction_DoesNotTurnATransportFailureIntoAnAnswer()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.transaction.get", _ => { server.DropClients(); return null; });
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.HasTransactionAsync(Sent, Net));
        Assert.IsNotType<ElectrumServerException>(ex);
    }
}
