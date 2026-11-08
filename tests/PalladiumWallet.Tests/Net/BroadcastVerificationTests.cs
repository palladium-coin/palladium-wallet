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
}
