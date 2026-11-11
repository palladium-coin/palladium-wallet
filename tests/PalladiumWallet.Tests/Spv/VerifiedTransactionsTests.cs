using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Core.Wallet;
using PalladiumWallet.Tests.Net;

namespace PalladiumWallet.Tests.Spv;

/// <summary>A raw transaction is accepted only under the txid it hashes to (ADR 0043).</summary>
public class VerifiedTransactionsTests
{
    private static readonly Network Net = PalladiumNetworks.Regtest;

    private static Transaction Tx(long sats, uint seed)
    {
        var tx = Net.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, seed)));
        tx.Outputs.Add(Money.Satoshis(sats), new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net));
        return tx;
    }

    [Fact]
    public void Accepts_a_transaction_under_its_own_txid()
    {
        var tx = Tx(1000, 0);
        var parsed = VerifiedTransactions.Parse(tx.ToHex(), tx.GetHash().ToString(), Net);
        Assert.Equal(tx.GetHash(), parsed.GetHash());
    }

    [Fact]
    public void Accepts_an_uppercase_txid()
    {
        var tx = Tx(1000, 0);
        VerifiedTransactions.Parse(tx.ToHex(), tx.GetHash().ToString().ToUpperInvariant(), Net);
    }

    [Fact]
    public void Rejects_a_different_transaction()
    {
        var wanted = Tx(1000, 0);
        var served = Tx(9000, 1);
        Assert.Throws<SpvVerificationException>(
            () => VerifiedTransactions.Parse(served.ToHex(), wanted.GetHash().ToString(), Net));
    }

    [Theory]
    [InlineData("")]
    [InlineData("zz")]
    [InlineData("abc")]
    public void Rejects_a_txid_that_is_not_a_hash(string txid)
    {
        Assert.Throws<SpvVerificationException>(
            () => VerifiedTransactions.Parse(Tx(1000, 0).ToHex(), txid, Net));
    }

    [Fact]
    public async Task The_inspector_refuses_a_transaction_that_does_not_match_the_requested_txid()
    {
        var wanted = Tx(1000, 0);
        var served = Tx(9000, 1);
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.transaction.get", _ => served.ToHex());
        await using var client = await PalladiumWallet.Core.Net.ElectrumClient.ConnectAsync(
            server.Host, server.Port, useSsl: false);

        await Assert.ThrowsAsync<SpvVerificationException>(() =>
            TransactionInspector.FetchAsync(client, Net, wanted.GetHash().ToString(), tipHeight: 10,
                height: 0, ownedAddresses: new HashSet<string>(), netSats: 0, verified: false));
    }
}
