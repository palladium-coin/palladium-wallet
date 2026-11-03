using System.Text.Json;
using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Tests.Net;

namespace PalladiumWallet.Tests.Spv;

/// <summary>
/// End-to-end tests of the SPV synchroniser against the in-process fake server:
/// gap-limit scanning, UTXO/history reconstruction, Merkle verification (the
/// path that must reject a lying server), busy retry, and the disk cache.
/// Every block in these scenarios contains a single transaction, so the Merkle
/// root is the txid itself and the branch is empty (the tree math has its own
/// dedicated tests in <see cref="MerkleProofTests"/>).
/// </summary>
public class WalletSynchronizerTests
{
    private static readonly ChainProfile Profile = ChainProfiles.Regtest;
    private static readonly Network Net = PalladiumNetworks.Regtest;

    private static HdAccount Account(ChainProfile? profile = null)
    {
        Assert.True(Bip39.TryParse(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            out var mnemonic));
        return HdAccount.FromMnemonic(mnemonic!, null, ScriptKind.NativeSegwit, profile ?? Profile);
    }

    /// <summary>
    /// Server-side chain state: transactions, per-scripthash histories and
    /// single-transaction block headers, wired onto a FakeElectrumServer.
    /// </summary>
    private sealed class Scenario
    {
        public int TipHeight = 200;
        public readonly Dictionary<string, Transaction> Txs = [];
        public readonly Dictionary<string, List<(string Txid, int Height)>> History = [];
        public readonly Dictionary<int, string> Headers = [];

        /// <summary>Creates a tx paying <paramref name="sats"/> to <paramref name="to"/> and registers it.</summary>
        public Transaction Pay(BitcoinAddress to, long sats, int height, OutPoint? from = null,
            bool coinbase = false, BitcoinAddress? changeTo = null, long changeSats = 0)
        {
            var tx = Net.CreateTransaction();
            tx.Inputs.Add(coinbase ? new TxIn() : new TxIn(from ?? new OutPoint(uint256.One, 0)));
            tx.Outputs.Add(Money.Satoshis(sats), to);
            if (changeTo is not null)
                tx.Outputs.Add(Money.Satoshis(changeSats), changeTo);
            Register(tx, height, to, changeTo);
            return tx;
        }

        public void Register(Transaction tx, int height, params BitcoinAddress?[] touchedAddresses)
        {
            var txid = tx.GetHash().ToString();
            Txs[txid] = tx;
            foreach (var addr in touchedAddresses)
            {
                if (addr is null) continue;
                var sh = Scripthash.FromAddress(addr);
                if (!History.TryGetValue(sh, out var list))
                    History[sh] = list = [];
                if (!list.Contains((txid, height)))
                    list.Add((txid, height));
            }
            if (height > 0 && !Headers.ContainsKey(height))
                Headers[height] = SingleTxHeaderHex(tx.GetHash(), height);
        }

        /// <summary>80-byte header of a block whose only transaction is <paramref name="txid"/>.</summary>
        public static string SingleTxHeaderHex(uint256 txid, int height, uint256? merkleRoot = null,
            uint256? prevHash = null)
        {
            var header = Net.Consensus.ConsensusFactory.CreateBlockHeader();
            header.HashPrevBlock = prevHash ?? uint256.Zero;
            header.HashMerkleRoot = merkleRoot ?? txid;
            header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + height);
            header.Bits = new Target(0x1d00ffffu);
            header.Nonce = (uint)height;
            return Convert.ToHexString(header.ToBytes()).ToLowerInvariant();
        }

        public void WireTo(FakeElectrumServer server)
        {
            server.Handle("blockchain.headers.subscribe",
                _ => new { height = TipHeight, hex = SingleTxHeaderHex(uint256.One, TipHeight) });
            server.Handle("blockchain.scripthash.subscribe", _ => null);
            server.Handle("blockchain.scripthash.get_history", p =>
                (History.GetValueOrDefault(p[0].GetString()!) ?? [])
                    .Select(h => new { tx_hash = h.Txid, height = h.Height }));
            server.Handle("blockchain.transaction.get", p =>
                Txs.TryGetValue(p[0].GetString()!, out var tx)
                    ? tx.ToHex()
                    : throw new FakeElectrumError(-32600, "no such transaction"));
            server.Handle("blockchain.transaction.get_merkle", p =>
                new { block_height = p[1].GetInt32(), pos = 0, merkle = Array.Empty<string>() });
            server.Handle("blockchain.block.header", p =>
                Headers.TryGetValue(p[0].GetInt32(), out var hex)
                    ? hex
                    : throw new FakeElectrumError(-32600, "no such block"));
            server.Handle("blockchain.block.headers", p =>
            {
                var start = p[0].GetInt32();
                var count = p[1].GetInt32();
                var hexes = new List<string>();
                for (var h = start; hexes.Count < count && Headers.TryGetValue(h, out var hex); h++)
                    hexes.Add(hex);
                return new { count = hexes.Count, hex = string.Concat(hexes) };
            });
        }
    }

    private static async Task<(FakeElectrumServer Server, ElectrumClient Client)> StartAsync(Scenario scenario)
    {
        var server = new FakeElectrumServer();
        scenario.WireTo(server);
        var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);
        return (server, client);
    }

    // ---- scansione ----

    [Fact]
    public async Task Wallet_vuoto_scansiona_il_gap_limit_e_riporta_saldo_zero()
    {
        var scenario = new Scenario();
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var result = await new WalletSynchronizer(Account(), client).SyncOnceAsync();

        Assert.Equal(200, result.TipHeight);
        Assert.Equal(0, result.ConfirmedSats);
        Assert.Equal(0, result.UnconfirmedSats);
        Assert.Equal(0, result.NextReceiveIndex);
        Assert.Equal(0, result.NextChangeIndex);
        // Default gap limit 20 on both chains.
        Assert.Equal(40, result.Addresses.Count);
        Assert.Equal(40, server.CallCount("blockchain.scripthash.get_history"));
        Assert.Empty(result.Utxos);
        Assert.Empty(result.History);
    }

    [Fact]
    public async Task La_scoperta_continua_oltre_il_primo_batch_quando_un_indirizzo_e_usato()
    {
        var account = Account();
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(3), 50_000, height: 100);
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var result = await new WalletSynchronizer(account, client, gapLimit: 5).SyncOnceAsync();

        // Index 3 used → the window slides: 5 more empty addresses (5..9) close the scan.
        Assert.Equal(4, result.NextReceiveIndex);
        Assert.Equal(10, result.Addresses.Count(a => !a.IsChange));
        Assert.Equal(5, result.Addresses.Count(a => a.IsChange));
        Assert.Equal(50_000, result.ConfirmedSats);
    }

    // ---- ricostruzione UTXO e storico ----

    [Fact]
    public async Task Una_ricezione_confermata_produce_utxo_saldo_e_storico_verificato()
    {
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var result = await new WalletSynchronizer(account, client).SyncOnceAsync();

        Assert.Equal(1_000_000, result.ConfirmedSats);
        Assert.Equal(0, result.ImmatureSats); // regtest: 1 conferma minima, ne ha 101
        Assert.Equal(1, result.NextReceiveIndex);

        var utxo = Assert.Single(result.Utxos);
        Assert.Equal(funding.GetHash().ToString(), utxo.Txid);
        Assert.Equal(100, utxo.Height);
        Assert.False(utxo.IsChange);

        var entry = Assert.Single(result.History);
        Assert.Equal(1_000_000, entry.DeltaSats);
        Assert.True(entry.Verified); // Merkle proof checked against the header

        var row = result.AddressRows.Single(r => r.Address == account.GetReceiveAddress(0).ToString());
        Assert.Equal(1_000_000, row.BalanceSats);
        Assert.Equal(1, row.TxCount);
    }

    [Fact]
    public async Task Una_spesa_rimuove_l_utxo_e_produce_il_delta_negativo()
    {
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);

        // Spend: 600k to an external address, 390k change to change/0, 10k fee.
        var external = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net);
        var spend = Net.CreateTransaction();
        spend.Inputs.Add(new TxIn(new OutPoint(funding, 0)));
        spend.Outputs.Add(Money.Satoshis(600_000), external);
        spend.Outputs.Add(Money.Satoshis(390_000), account.GetChangeAddress(0));
        scenario.Register(spend, 101, account.GetReceiveAddress(0), account.GetChangeAddress(0));

        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var result = await new WalletSynchronizer(account, client).SyncOnceAsync();

        // The funding UTXO is spent: only the change remains.
        var utxo = Assert.Single(result.Utxos);
        Assert.Equal(spend.GetHash().ToString(), utxo.Txid);
        Assert.True(utxo.IsChange);
        Assert.Equal(390_000, result.ConfirmedSats);
        Assert.Equal(1, result.NextChangeIndex);

        // History newest-first with the net deltas.
        Assert.Equal(2, result.History.Count);
        Assert.Equal(spend.GetHash().ToString(), result.History[0].Txid);
        Assert.Equal(-610_000, result.History[0].DeltaSats); // 390k change − 1M spent
        Assert.Equal(1_000_000, result.History[1].DeltaSats);
    }

    [Fact]
    public async Task Una_tx_in_mempool_conta_nel_saldo_non_confermato_e_non_verifica_merkle()
    {
        var account = Account();
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(0), 250_000, height: 0);
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var result = await new WalletSynchronizer(account, client).SyncOnceAsync();

        Assert.Equal(0, result.ConfirmedSats);
        Assert.Equal(250_000, result.UnconfirmedSats);
        var entry = Assert.Single(result.History);
        // Verified means "its Merkle proof was checked" — a mempool tx has no proof to
        // check yet, so it's vacuously true; "not confirmed" is signalled by Height <= 0,
        // not by Verified (no merkle/header RPCs happen, asserted below).
        Assert.True(entry.Verified);
        Assert.Equal(0, server.CallCount("blockchain.transaction.get_merkle"));
        Assert.Equal(0, server.CallCount("blockchain.block.header"));
    }

    [Fact]
    public async Task Un_coinbase_immaturo_finisce_nel_saldo_immaturo()
    {
        var account = Account();
        var scenario = new Scenario { TipHeight = 200 };
        // 11 confirmations at tip 200: far below CoinbaseMaturity+1 = 121.
        scenario.Pay(account.GetReceiveAddress(0), 5_000_000_000, height: 190, coinbase: true);
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var result = await new WalletSynchronizer(account, client).SyncOnceAsync();

        Assert.Equal(5_000_000_000, result.ConfirmedSats);
        Assert.Equal(5_000_000_000, result.ImmatureSats);
        Assert.True(Assert.Single(result.Utxos).IsCoinbase);
    }

    // ---- sicurezza SPV ----

    [Fact]
    public async Task Una_prova_merkle_che_non_torna_con_l_header_fa_fallire_la_sync()
    {
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);
        // The server lies: the block 100 header commits to a different Merkle root.
        scenario.Headers[100] = Scenario.SingleTxHeaderHex(
            funding.GetHash(), 100, merkleRoot: uint256.One);

        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var ex = await Assert.ThrowsAsync<SpvVerificationException>(
            () => new WalletSynchronizer(account, client).SyncOnceAsync());
        Assert.Contains(funding.GetHash().ToString(), ex.Message);
    }

    // ---- checkpoint anchoring ----

    /// <summary>
    /// Builds a chain of single-tx headers from <paramref name="fromHeight"/> to
    /// <paramref name="toHeight"/> (inclusive), each linked to the previous via
    /// HashPrevBlock. The last height carries <paramref name="txid"/> as its Merkle
    /// root; <paramref name="roots"/> overrides the root at specific heights.
    /// </summary>
    private static Dictionary<int, string> ChainedHeaders(int fromHeight, int toHeight, uint256 txid,
        Dictionary<int, uint256>? roots = null)
    {
        var headers = new Dictionary<int, string>();
        uint256? prevHash = null;
        for (var h = fromHeight; h <= toHeight; h++)
        {
            var root = roots?.GetValueOrDefault(h) ?? (h == toHeight ? txid : uint256.One);
            var hex = Scenario.SingleTxHeaderHex(root, h, merkleRoot: root, prevHash: prevHash);
            headers[h] = hex;
            prevHash = BlockHeaderInfo.Parse(hex).Hash;
        }
        return headers;
    }

    [Fact]
    public async Task Una_tx_anchorata_a_un_checkpoint_valido_verifica_la_catena_di_header()
    {
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);
        var chain = ChainedHeaders(100, 105, funding.GetHash(), roots: new() { [100] = funding.GetHash() });
        foreach (var (h, hex) in chain) scenario.Headers[h] = hex;

        // The checkpoint sits ABOVE the tx (105): the header at 100 must hash-chain up to it.
        var checkpointProfile = Profile with
        {
            Checkpoints = [new Checkpoint(105, BlockHeaderInfo.Parse(chain[105]).Hash.ToString(), 0x1d00ffff)],
        };
        var checkpointAccount = Account(checkpointProfile);

        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var result = await new WalletSynchronizer(checkpointAccount, client).SyncOnceAsync();

        Assert.Equal(1_000_000, result.ConfirmedSats);
        // Anchoring runs before the header lookup, so the range call (blockchain.block.headers)
        // covering 100..105 up to the checkpoint already includes the tx's own header —
        // no separate single-header call needed.
        Assert.Equal(0, server.CallCount("blockchain.block.header"));
        Assert.Equal(1, server.CallCount("blockchain.block.headers"));
    }

    [Fact]
    public async Task Un_range_gia_ancorato_non_viene_ricamminato_al_sync_successivo()
    {
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 103);

        // Second tx (at height 105) known upfront so the chain can commit to it,
        // but announced by the server only after the first sync.
        var tx2 = Net.CreateTransaction();
        tx2.Inputs.Add(new TxIn(new OutPoint(uint256.One, 1)));
        tx2.Outputs.Add(Money.Satoshis(500_000), account.GetReceiveAddress(1));

        var chain = ChainedHeaders(100, 105, funding.GetHash(),
            roots: new() { [103] = funding.GetHash(), [105] = tx2.GetHash() });
        foreach (var (h, hex) in chain) scenario.Headers[h] = hex;

        var checkpointProfile = Profile with
        {
            Checkpoints = [new Checkpoint(105, BlockHeaderInfo.Parse(chain[105]).Hash.ToString(), 0x1d00ffff)],
        };
        var checkpointAccount = Account(checkpointProfile);

        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var sync = new WalletSynchronizer(checkpointAccount, client);
        await sync.SyncOnceAsync(); // walks 103..105 up to the checkpoint and memoizes it

        // 105 lies inside the memoized [103, 105]: anchoring must early-return without
        // re-walking, so the header/range call counts stay at those of the first walk.
        scenario.Register(tx2, 105, checkpointAccount.GetReceiveAddress(1));
        var result = await sync.SyncOnceAsync();

        Assert.Equal(1_500_000, result.ConfirmedSats);
        Assert.Equal(0, server.CallCount("blockchain.block.header"));
        Assert.Equal(1, server.CallCount("blockchain.block.headers"));
    }

    [Fact]
    public async Task Extending_the_anchor_downwards_only_walks_the_new_segment()
    {
        var account = Account();
        var scenario = new Scenario();
        var high = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 104);
        var tx2 = Net.CreateTransaction();
        tx2.Inputs.Add(new TxIn(new OutPoint(uint256.One, 1)));
        tx2.Outputs.Add(Money.Satoshis(500_000), account.GetReceiveAddress(1));
        var chain = ChainedHeaders(100, 105, high.GetHash(),
            roots: new() { [104] = high.GetHash(), [101] = tx2.GetHash() });
        foreach (var (h, hex) in chain) scenario.Headers[h] = hex;
        var checkpointAccount = Account(Profile with
        {
            Checkpoints = [new Checkpoint(105, BlockHeaderInfo.Parse(chain[105]).Hash.ToString(), 0x1d00ffff)],
        });
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var sync = new WalletSynchronizer(checkpointAccount, client);
        await sync.SyncOnceAsync();                                   // anchors [104, 105]
        scenario.Register(tx2, 101, checkpointAccount.GetReceiveAddress(1));
        var result = await sync.SyncOnceAsync();                      // extends to [101, 105]

        Assert.Equal(1_500_000, result.ConfirmedSats);
        Assert.Equal(2, server.CallCount("blockchain.block.headers")); // 104..105, then 101..103
        Assert.Equal(0, server.CallCount("blockchain.block.header"));
    }

    [Fact]
    public async Task A_server_that_forges_a_consistent_chain_to_the_checkpoint_is_detected()
    {
        // The attack the anchoring must stop: the server invents the header of the tx's block and
        // chains fake headers after it, all with valid prev-hash links. Linking "from a checkpoint
        // upward" would accept this; linking "up to a checkpoint" cannot, because the chain does
        // not end at the real hardcoded hash.
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);

        var honest = ChainedHeaders(100, 105, uint256.One);     // the real chain (tx not in it)
        var forged = ChainedHeaders(100, 105, funding.GetHash(),
            roots: new() { [100] = funding.GetHash() });         // fabricated, self-consistent
        foreach (var (h, hex) in forged) scenario.Headers[h] = hex;

        var checkpointAccount = Account(Profile with
        {
            Checkpoints = [new Checkpoint(105, BlockHeaderInfo.Parse(honest[105]).Hash.ToString(), 0x1d00ffff)],
        });
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var ex = await Assert.ThrowsAsync<SpvVerificationException>(
            () => new WalletSynchronizer(checkpointAccount, client).SyncOnceAsync());
        Assert.Contains("checkpoint height 105", ex.Message);
    }

    [Fact]
    public async Task A_tx_above_the_last_checkpoint_has_nothing_to_anchor_to()
    {
        // Documented limit (SECURITY.md): only the Merkle proof is checked there.
        var account = Account();
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 150);
        var checkpointAccount = Account(Profile with
        {
            Checkpoints = [new Checkpoint(100, uint256.One.ToString(), 0x1d00ffff)],
        });
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var result = await new WalletSynchronizer(checkpointAccount, client).SyncOnceAsync();

        Assert.Equal(1_000_000, result.ConfirmedSats);
        Assert.Equal(0, server.CallCount("blockchain.block.headers"));
    }

    [Fact]
    public async Task A_tx_below_the_first_checkpoint_is_anchored_to_it()
    {
        // L-01: heights under the first checkpoint used to have no anchor at all.
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 10);
        var chain = ChainedHeaders(10, 100, funding.GetHash(), roots: new() { [10] = funding.GetHash() });
        foreach (var (h, hex) in chain) scenario.Headers[h] = hex;
        var checkpointAccount = Account(Profile with
        {
            Checkpoints = [new Checkpoint(100, BlockHeaderInfo.Parse(chain[100]).Hash.ToString(), 0x1d00ffff)],
        });
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var result = await new WalletSynchronizer(checkpointAccount, client).SyncOnceAsync();

        Assert.Equal(1_000_000, result.ConfirmedSats);
        Assert.True(server.CallCount("blockchain.block.headers") >= 1);
    }

    [Fact]
    public async Task A_cache_without_the_anchor_state_is_not_trusted_for_verified_proofs()
    {
        // A cache written before the anchoring direction was fixed has no AnchoredDownTo: its
        // "verified" claims are dropped and every proof is checked again.
        var account = Account();
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;
        var first = new WalletSynchronizer(account, client);
        var result1 = await first.SyncOnceAsync();
        var (rawTx, verifiedAt, headers, _) = first.ExportCaches(Net);
        Assert.NotEmpty(verifiedAt);

        server.ResetCallCounts();
        var second = new WalletSynchronizer(account, client);
        second.PreloadCaches(rawTx, verifiedAt, headers,
            result1.NextReceiveIndex, result1.NextChangeIndex, Net, anchoredDownTo: null);
        var result2 = await second.SyncOnceAsync();

        Assert.Equal(result1.ConfirmedSats, result2.ConfirmedSats);
        Assert.Equal(1, server.CallCount("blockchain.transaction.get_merkle")); // re-verified
        Assert.Equal(0, server.CallCount("blockchain.transaction.get"));        // raw tx still reused
    }

    [Fact]
    public async Task Un_checkpoint_con_hash_sbagliato_fa_fallire_la_sync()
    {
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);
        var chain = ChainedHeaders(100, 105, funding.GetHash(), roots: new() { [100] = funding.GetHash() });
        foreach (var (h, hex) in chain) scenario.Headers[h] = hex;

        var checkpointProfile = Profile with
        {
            // Wrong hash at the checkpoint height: the real chain does not match it.
            Checkpoints = [new Checkpoint(105, uint256.One.ToString(), 0x1d00ffff)],
        };
        var checkpointAccount = Account(checkpointProfile);

        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var ex = await Assert.ThrowsAsync<SpvVerificationException>(
            () => new WalletSynchronizer(checkpointAccount, client).SyncOnceAsync());
        Assert.Contains("checkpoint height 105", ex.Message);
    }

    [Fact]
    public async Task Una_catena_di_header_spezzata_fa_fallire_la_sync()
    {
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);
        var chain = ChainedHeaders(100, 105, funding.GetHash(), roots: new() { [100] = funding.GetHash() });
        // Tamper with block 103: it no longer points at block 102's real hash.
        chain[103] = Scenario.SingleTxHeaderHex(uint256.One, 103, prevHash: uint256.One);
        foreach (var (h, hex) in chain) scenario.Headers[h] = hex;

        var checkpointProfile = Profile with
        {
            Checkpoints = [new Checkpoint(105, BlockHeaderInfo.Parse(chain[105]).Hash.ToString(), 0x1d00ffff)],
        };
        var checkpointAccount = Account(checkpointProfile);

        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var ex = await Assert.ThrowsAsync<SpvVerificationException>(
            () => new WalletSynchronizer(checkpointAccount, client).SyncOnceAsync());
        Assert.Contains("Broken header chain at height 103", ex.Message);
    }

    [Fact]
    public async Task A_server_returning_no_headers_during_anchoring_fails_the_sync()
    {
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);
        var chain = ChainedHeaders(100, 105, funding.GetHash(), roots: new() { [100] = funding.GetHash() });
        foreach (var (h, hex) in chain) scenario.Headers[h] = hex;

        var checkpointAccount = Account(Profile with
        {
            Checkpoints = [new Checkpoint(105, BlockHeaderInfo.Parse(chain[105]).Hash.ToString(), 0x1d00ffff)],
        });

        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;
        // A dishonest/pruned server answers the range request with zero headers.
        server.Handle("blockchain.block.headers", _ => new { count = 0, hex = "" });

        var ex = await Assert.ThrowsAsync<SpvVerificationException>(
            () => new WalletSynchronizer(checkpointAccount, client).SyncOnceAsync());
        Assert.Contains("returned no headers", ex.Message);
    }

    [Fact]
    public async Task Senza_checkpoint_a_copertura_dell_altezza_l_anchoring_e_un_no_op()
    {
        // Regtest profile (Profile) has no checkpoints at all: same behaviour as before
        // this feature existed — only the Merkle proof is checked.
        var account = Account();
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var result = await new WalletSynchronizer(account, client).SyncOnceAsync();

        Assert.Equal(1_000_000, result.ConfirmedSats);
        Assert.Equal(1, server.CallCount("blockchain.block.header"));
    }

    // ---- verifica progressiva (§7.4) ----

    [Fact]
    public async Task PartialResult_arriva_prima_della_proof_poi_il_risultato_finale_e_completamente_verificato()
    {
        var account = Account();
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);

        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        // Gate the Merkle-proof response so the download phase can complete (and fire
        // PartialResult) well before verification does.
        using var gate = new ManualResetEventSlim(false);
        server.Handle("blockchain.transaction.get_merkle", p =>
        {
            gate.Wait();
            return new { block_height = p[1].GetInt32(), pos = 0, merkle = Array.Empty<string>() };
        });

        var sync = new WalletSynchronizer(account, client);
        SyncResult? partial = null;
        var partialReceived = new TaskCompletionSource();
        sync.PartialResult += r => { partial = r; partialReceived.TrySetResult(); };

        var syncTask = sync.SyncOnceAsync();
        await partialReceived.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(partial);
        Assert.False(Assert.Single(partial!.History).Verified);
        Assert.False(Assert.Single(partial.Utxos).Verified);
        Assert.Equal(1_000_000, partial.PendingVerificationSats);
        Assert.Equal(1_000_000, partial.ConfirmedSats); // reported confirmed, just not yet proof-checked

        gate.Set();
        var final = await syncTask;

        Assert.True(Assert.Single(final.History).Verified);
        Assert.True(Assert.Single(final.Utxos).Verified);
        Assert.Equal(0, final.PendingVerificationSats);
    }

    [Fact]
    public async Task Un_coinbase_immaturo_e_non_ancora_verificato_non_produce_un_saldo_spendibile_negativo()
    {
        // Regression: ImmatureSats and PendingVerificationSats can overlap (an immature
        // coinbase can also be unverified) — "ConfirmedSats - ImmatureSats - PendingVerificationSats"
        // double-subtracts that overlap and can go negative. SpendableSats must be computed
        // directly from IsSpendable instead.
        var account = Account();
        var scenario = new Scenario { TipHeight = 200 };
        // 11 confirmations at tip 200: far below CoinbaseMaturity+1 = 121 — immature.
        scenario.Pay(account.GetReceiveAddress(0), 5_000_000_000, height: 190, coinbase: true);

        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        using var gate = new ManualResetEventSlim(false);
        server.Handle("blockchain.transaction.get_merkle", p =>
        {
            gate.Wait();
            return new { block_height = p[1].GetInt32(), pos = 0, merkle = Array.Empty<string>() };
        });

        var sync = new WalletSynchronizer(account, client);
        var partialReceived = new TaskCompletionSource();
        SyncResult? partial = null;
        sync.PartialResult += r => { partial = r; partialReceived.TrySetResult(); };

        var syncTask = sync.SyncOnceAsync();
        await partialReceived.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Immature AND unverified at the same time: both non-spendable subsets are the
        // full 5 PLM, but the actual spendable balance must be exactly zero, not negative.
        Assert.Equal(5_000_000_000, partial!.ImmatureSats);
        Assert.Equal(5_000_000_000, partial.PendingVerificationSats);
        Assert.Equal(0, partial.SpendableSats);

        gate.Set();
        await syncTask;
    }

    // ---- resilienza ----

    [Fact]
    public async Task Gli_errori_server_busy_vengono_ritentati_fino_al_successo()
    {
        var account = Account();
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);

        var server = new FakeElectrumServer();
        scenario.WireTo(server);
        // The first 3 history calls fail with the ElectrumX throttling error.
        var failures = 3;
        var histories = scenario.History;
        server.Handle("blockchain.scripthash.get_history", p =>
        {
            if (Interlocked.Decrement(ref failures) >= 0)
                throw new FakeElectrumError(-102, "excessive resource usage");
            return (histories.GetValueOrDefault(p[0].GetString()!) ?? [])
                .Select(h => new { tx_hash = h.Txid, height = h.Height });
        });

        await using var _ = server;
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var result = await new WalletSynchronizer(account, client).SyncOnceAsync();
        Assert.Equal(1_000_000, result.ConfirmedSats);
    }

    [Fact]
    public async Task I_download_di_transazioni_su_server_busy_vengono_ritentati()
    {
        // Same throttling scenario but on transaction.get, which runs inside the
        // download tasks (the non-generic retry overload, unlike get_history).
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);

        var server = new FakeElectrumServer();
        scenario.WireTo(server);
        var failures = 2;
        var txs = scenario.Txs;
        server.Handle("blockchain.transaction.get", p =>
        {
            if (Interlocked.Decrement(ref failures) >= 0)
                throw new FakeElectrumError(-101, "server busy");
            return txs[p[0].GetString()!].ToHex();
        });

        await using var _ = server;
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var result = await new WalletSynchronizer(account, client).SyncOnceAsync();
        Assert.Equal(1_000_000, result.ConfirmedSats);
        Assert.Equal(funding.GetHash().ToString(), Assert.Single(result.History).Txid);
    }

    // ---- cache su disco ----

    [Fact]
    public async Task La_cache_precaricata_evita_di_riscaricare_tx_prove_e_header()
    {
        var account = Account();
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var first = new WalletSynchronizer(account, client);
        var result1 = await first.SyncOnceAsync();
        var (rawTx, verifiedAt, headers, anchoredDownTo) = first.ExportCaches(Net);

        Assert.Single(rawTx);      // the confirmed tx is exported
        Assert.Single(verifiedAt); // with its verified height
        Assert.NotEmpty(headers);

        // Fresh synchroniser (new launch), warm cache: no re-download.
        server.ResetCallCounts();
        var second = new WalletSynchronizer(account, client);
        second.PreloadCaches(rawTx, verifiedAt, headers,
            result1.NextReceiveIndex, result1.NextChangeIndex, Net, anchoredDownTo);
        var result2 = await second.SyncOnceAsync();

        Assert.Equal(result1.ConfirmedSats, result2.ConfirmedSats);
        Assert.Equal(0, server.CallCount("blockchain.transaction.get"));
        Assert.Equal(0, server.CallCount("blockchain.transaction.get_merkle"));
        Assert.Equal(0, server.CallCount("blockchain.block.header"));
    }

    [Fact]
    public async Task Lo_stato_di_anchoring_precaricato_da_disco_evita_di_ricamminare_la_catena_al_riavvio()
    {
        // Same scenario as "Un_range_gia_ancorato_non_viene_ricamminato_al_sync_successivo", but
        // across two separate WalletSynchronizer instances (simulating an app restart) with
        // AnchoredDownTo round-tripped through ExportCaches/PreloadCaches like a real save/reload —
        // the in-memory-only memoization that test covers wouldn't survive that on its own.
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 103);

        var tx2 = Net.CreateTransaction();
        tx2.Inputs.Add(new TxIn(new OutPoint(uint256.One, 1)));
        tx2.Outputs.Add(Money.Satoshis(500_000), account.GetReceiveAddress(1));

        var chain = ChainedHeaders(100, 105, funding.GetHash(),
            roots: new() { [103] = funding.GetHash(), [105] = tx2.GetHash() });
        foreach (var (h, hex) in chain) scenario.Headers[h] = hex;

        var checkpointProfile = Profile with
        {
            Checkpoints = [new Checkpoint(105, BlockHeaderInfo.Parse(chain[105]).Hash.ToString(), 0x1d00ffff)],
        };
        var checkpointAccount = Account(checkpointProfile);

        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var first = new WalletSynchronizer(checkpointAccount, client);
        var result1 = await first.SyncOnceAsync(); // walks 103..105 and memoizes the anchor
        var (rawTx, verifiedAt, headers, anchoredDownTo) = first.ExportCaches(Net);
        Assert.NotEmpty(anchoredDownTo);

        // Fresh synchroniser (new launch) preloaded from the exported caches, then a new tx at
        // height 105 — within the already-anchored range — is announced.
        scenario.Register(tx2, 105, checkpointAccount.GetReceiveAddress(1));
        server.ResetCallCounts();
        var second = new WalletSynchronizer(checkpointAccount, client);
        second.PreloadCaches(rawTx, verifiedAt, headers,
            result1.NextReceiveIndex, result1.NextChangeIndex, Net, anchoredDownTo);
        var result2 = await second.SyncOnceAsync();

        // 105 lies in the persisted anchored range [103, 105]: no header-range re-walk despite this being a
        // brand-new synchroniser instance that never anchored anything itself.
        Assert.Equal(1_500_000, result2.ConfirmedSats);
        Assert.Equal(0, server.CallCount("blockchain.block.header"));
        Assert.Equal(0, server.CallCount("blockchain.block.headers"));
    }

    [Fact]
    public async Task Le_tx_non_confermate_non_vengono_esportate_nella_cache()
    {
        var account = Account();
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(0), 250_000, height: 0); // mempool
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var sync = new WalletSynchronizer(account, client);
        await sync.SyncOnceAsync();
        var (rawTx, verifiedAt, _, _) = sync.ExportCaches(Net);

        // Unconfirmed txs can change (RBF): they must always be re-downloaded.
        Assert.Empty(rawTx);
        Assert.Empty(verifiedAt);
    }

    // ---- account a indirizzi fissi (WIF importati) ----

    [Fact]
    public async Task Un_account_con_indirizzi_fissi_scansiona_solo_quelli()
    {
        var key = new Key();
        var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Net);
        var account = new ImportedKeyAccount([(address, key)], ScriptKind.NativeSegwit, Profile);

        var scenario = new Scenario();
        scenario.Pay(address, 750_000, height: 150);
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var result = await new WalletSynchronizer(account, client).SyncOnceAsync();

        Assert.Equal(750_000, result.ConfirmedSats);
        Assert.Single(result.Addresses);
        Assert.Equal(1, server.CallCount("blockchain.scripthash.get_history"));
        Assert.Equal(1, result.NextReceiveIndex);
    }

    // ---- cross-check of recent headers against a witness server (ADR 0042) ----

    private static ChainProfile ProfileWithCheckpointAt100() => Profile with
    {
        // Newest checkpoint at 100: a tx at height 150 has nothing to anchor to.
        Checkpoints = [new Checkpoint(100, uint256.One.ToString(), 0x1d00ffff)],
    };

    /// <summary>A witness server answering blockchain.block.header with <paramref name="headerAt150"/>.</summary>
    private static async Task<(FakeElectrumServer Server, ElectrumClient Client)> StartWitnessAsync(
        Func<int, string> headerAt)
    {
        var server = new FakeElectrumServer();
        server.Handle("blockchain.block.header", p => headerAt(p[0].GetInt32()));
        var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);
        return (server, client);
    }

    private static Task<IReadOnlyList<ElectrumClient>> Witnesses(params ElectrumClient[] clients) =>
        Task.FromResult<IReadOnlyList<ElectrumClient>>(clients);

    [Fact]
    public async Task A_witness_that_agrees_lets_a_recent_proof_verify()
    {
        var account = Account(ProfileWithCheckpointAt100());
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 150);
        var (server, client) = await StartAsync(scenario);
        var (witness, witnessClient) = await StartWitnessAsync(_ => scenario.Headers[150]);
        await using var _ = server; await using var __ = client;
        await using var ___ = witness; await using var ____ = witnessClient;

        var sync = new WalletSynchronizer(account, client) { WitnessProvider = _ => Witnesses(witnessClient) };
        var result = await sync.SyncOnceAsync();

        Assert.Equal(1_000_000, result.SpendableSats);
        Assert.Equal(0, result.PendingVerificationSats);
        Assert.Equal(1, witness.CallCount("blockchain.block.header"));
    }

    [Fact]
    public async Task A_witness_that_disagrees_at_a_safe_depth_fails_the_sync()
    {
        var account = Account(ProfileWithCheckpointAt100());
        var scenario = new Scenario(); // tip 200, tx at 150: depth 50
        scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 150);
        var (server, client) = await StartAsync(scenario);
        var other = Scenario.SingleTxHeaderHex(uint256.One, 150); // a different header at 150
        var (witness, witnessClient) = await StartWitnessAsync(_ => other);
        await using var _ = server; await using var __ = client;
        await using var ___ = witness; await using var ____ = witnessClient;

        var sync = new WalletSynchronizer(account, client) { WitnessProvider = _ => Witnesses(witnessClient) };

        var ex = await Assert.ThrowsAsync<SpvVerificationException>(() => sync.SyncOnceAsync());
        Assert.Contains("disagree about the header at height 150", ex.Message);
    }

    [Fact]
    public async Task A_disagreement_at_a_shallow_depth_leaves_the_proof_unverified_without_failing()
    {
        var account = Account(ProfileWithCheckpointAt100());
        var scenario = new Scenario { TipHeight = 151 }; // tx at 150: depth 1, could be a reorg
        scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 150);
        var (server, client) = await StartAsync(scenario);
        var (witness, witnessClient) = await StartWitnessAsync(_ => Scenario.SingleTxHeaderHex(uint256.One, 150));
        await using var _ = server; await using var __ = client;
        await using var ___ = witness; await using var ____ = witnessClient;

        var sync = new WalletSynchronizer(account, client) { WitnessProvider = _ => Witnesses(witnessClient) };
        var result = await sync.SyncOnceAsync();

        Assert.Equal(0, result.SpendableSats);                 // not spendable
        Assert.Equal(1_000_000, result.ConfirmedSats);         // but still reported
        Assert.False(Assert.Single(result.History).Verified);  // and not marked verified (no caching)
        Assert.Empty(sync.ExportCaches(Net).VerifiedAt);

        // The witness now agrees (the servers converged): the next sync verifies it.
        witness.Handle("blockchain.block.header", _ => scenario.Headers[150]);
        var again = await sync.SyncOnceAsync();
        Assert.Equal(0, again.PendingVerificationSats);
        Assert.True(Assert.Single(again.History).Verified);
    }

    [Fact]
    public async Task A_witness_that_does_not_know_the_block_or_is_gone_is_skipped()
    {
        var account = Account(ProfileWithCheckpointAt100());
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 150);
        var (server, client) = await StartAsync(scenario);
        var (witness, witnessClient) = await StartWitnessAsync(_ => throw new FakeElectrumError(-1, "no such block"));
        await using var _ = server; await using var __ = client;
        await using var ___ = witness;

        var behind = await new WalletSynchronizer(account, client) { WitnessProvider = _ => Witnesses(witnessClient) }
            .SyncOnceAsync();
        Assert.Equal(1_000_000, behind.SpendableSats);

        await witnessClient.DisposeAsync(); // a dead witness connection
        var gone = await new WalletSynchronizer(account, client) { WitnessProvider = _ => Witnesses(witnessClient) }
            .SyncOnceAsync();
        Assert.Equal(1_000_000, gone.SpendableSats);
    }

    [Fact]
    public async Task Without_witnesses_the_behaviour_is_unchanged()
    {
        var account = Account(ProfileWithCheckpointAt100());
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 150);
        var (server, client) = await StartAsync(scenario);
        await using var _ = server; await using var __ = client;

        var none = await new WalletSynchronizer(account, client).SyncOnceAsync();
        var empty = await new WalletSynchronizer(account, client) { WitnessProvider = _ => Witnesses() }.SyncOnceAsync();

        Assert.Equal(1_000_000, none.SpendableSats);
        Assert.Equal(1_000_000, empty.SpendableSats);
    }

    [Fact]
    public async Task Heights_covered_by_a_checkpoint_are_not_cross_checked()
    {
        var account = Account();
        var scenario = new Scenario();
        var funding = scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 100);
        var chain = ChainedHeaders(100, 105, funding.GetHash(), roots: new() { [100] = funding.GetHash() });
        foreach (var (h, hex) in chain) scenario.Headers[h] = hex;
        var checkpointAccount = Account(Profile with
        {
            Checkpoints = [new Checkpoint(105, BlockHeaderInfo.Parse(chain[105]).Hash.ToString(), 0x1d00ffff)],
        });
        var (server, client) = await StartAsync(scenario);
        var (witness, witnessClient) = await StartWitnessAsync(_ => "00");
        await using var _ = server; await using var __ = client;
        await using var ___ = witness; await using var ____ = witnessClient;

        var sync = new WalletSynchronizer(checkpointAccount, client) { WitnessProvider = _ => Witnesses(witnessClient) };
        var result = await sync.SyncOnceAsync();

        Assert.Equal(1_000_000, result.ConfirmedSats);
        Assert.Equal(0, witness.CallCount("blockchain.block.header"));
    }

    [Fact]
    public async Task The_witness_is_asked_once_per_height_and_the_provider_once_per_synchronizer()
    {
        var account = Account(ProfileWithCheckpointAt100());
        var scenario = new Scenario();
        scenario.Pay(account.GetReceiveAddress(0), 1_000_000, height: 150);
        scenario.Pay(account.GetReceiveAddress(1), 500_000, height: 160);
        var (server, client) = await StartAsync(scenario);
        var (witness, witnessClient) = await StartWitnessAsync(h => scenario.Headers[h]);
        await using var _ = server; await using var __ = client;
        await using var ___ = witness; await using var ____ = witnessClient;
        var providerCalls = 0;

        var sync = new WalletSynchronizer(account, client)
        {
            WitnessProvider = _ => { Interlocked.Increment(ref providerCalls); return Witnesses(witnessClient); },
        };
        var result = await sync.SyncOnceAsync();
        await sync.SyncOnceAsync();

        Assert.Equal(1_500_000, result.SpendableSats);
        Assert.Equal(2, witness.CallCount("blockchain.block.header")); // heights 150 and 160, once each
        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public async Task WitnessConnector_skips_the_primary_and_wrong_chain_servers_and_respects_the_maximum()
    {
        await using var primary = new FakeElectrumServer();
        await using var wrongChain = new FakeElectrumServer();
        wrongChain.Handle("server.features", _ => new { genesis_hash = ChainProfiles.Testnet.GenesisHash });
        await using var good1 = new FakeElectrumServer();
        await using var good2 = new FakeElectrumServer();
        foreach (var f in new[] { primary, good1, good2 }) // the chain check reads the tip
            f.Handle("blockchain.headers.subscribe", _ => new { height = 10, hex = new string('0', 160) });
        var candidates = new[] { primary, wrongChain, good1, good2 }
            .Select(f => new ServerEndpoint(f.Host, f.Port, f.Port)).ToArray();
        // an unreachable candidate in front too
        candidates = [new ServerEndpoint("127.0.0.1", 1, 1), .. candidates];
        var profile = ChainProfiles.Mainnet with { Checkpoints = [] };

        var witnesses = await WitnessConnector.ConnectAsync(profile, candidates,
            primary.Host, primary.Port, useSsl: false, pins: null, max: 1);
        try
        {
            var w = Assert.Single(witnesses);
            Assert.Equal(good1.Port, w.Port); // not the primary, not the wrong chain, not the dead port
            Assert.Equal(0, good2.CallCount("server.version")); // stopped at the maximum
        }
        finally { foreach (var w in witnesses) await w.DisposeAsync(); }

        var two = await WitnessConnector.ConnectAsync(profile, candidates,
            primary.Host, primary.Port, useSsl: false, pins: null, max: 5);
        try { Assert.Equal([good1.Port, good2.Port], two.Select(c => c.Port).ToArray()); }
        finally { foreach (var w in two) await w.DisposeAsync(); }
    }
}
