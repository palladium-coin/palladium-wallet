using System.Reflection;
using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Tests.Net;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// End-to-end tests of the CLI: the real entry point is invoked in-process with a captured
/// console. State is sandboxed: wallet files live in a temp folder (--file) and the data
/// root (server list, certificate pins) is redirected to it.
/// </summary>
[Collection("AppPaths")] // Console.SetOut and AppPaths are process-wide
public sealed class CliTests : IDisposable
{
    private const string Mnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"plm-cli-{Guid.NewGuid()}");
    private readonly string? _savedOverride = AppPaths.OverrideDataRoot;

    public CliTests()
    {
        Directory.CreateDirectory(_dir);
        AppPaths.OverrideDataRoot = _dir;
    }

    public void Dispose()
    {
        AppPaths.OverrideDataRoot = _savedOverride;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WalletFile(string name = "w.json") => Path.Combine(_dir, name);

    private static Task<(int Exit, string Out, string Err)> Cli(params string[] args) => CliWithStdin(null, args);

    private static async Task<(int Exit, string Out, string Err)> CliWithStdin(string? stdin, params string[] args)
    {
        var entry = Assembly.Load("PalladiumWallet.Cli").EntryPoint
            ?? throw new InvalidOperationException("CLI has no entry point.");
        var (oldOut, oldErr, oldIn) = (Console.Out, Console.Error, Console.In);
        var (o, e) = (new StringWriter(), new StringWriter());
        Console.SetOut(o); Console.SetError(e); Console.SetIn(new StringReader(stdin ?? ""));
        try
        {
            // The top-level-statements entry point is a synchronous Main(string[]) → int wrapper.
            var exit = await Task.Run(() => (int)entry.Invoke(null, [args])!);
            return (exit, o.ToString(), e.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); Console.SetIn(oldIn); }
    }

    [Fact]
    public async Task NoArguments_PrintsUsage_AndFails()
    {
        var (exit, output, _) = await Cli();
        Assert.Equal(1, exit);
        Assert.Contains("PalladiumWallet CLI", output);
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData("24", 24)]
    public async Task NewSeed_GeneratesAValidMnemonicOfTheRequestedLength(string words, int count)
    {
        var (exit, output, _) = await Cli("newseed", "--words", words);
        Assert.Equal(0, exit);
        Assert.True(Bip39.TryParse(output.Trim(), out var m));
        Assert.Equal(count, m!.Words.Length);
    }

    [Fact]
    public async Task Addresses_MatchesTheCoreDerivation()
    {
        var (exit, output, _) = await Cli("addresses", Mnemonic, "--net", "regtest", "--count", "3");
        Assert.Equal(0, exit);
        Assert.True(Bip39.TryParse(Mnemonic, out var m));
        var account = HdAccount.FromMnemonic(m!, null, ScriptKind.NativeSegwit, ChainProfiles.Regtest);
        for (var i = 0; i < 3; i++)
            Assert.Contains(account.GetReceiveAddress(i).ToString(), output);
        Assert.Contains(account.ToSlip132(), output);
    }

    [Fact]
    public async Task Restore_WithAnInvalidMnemonic_FailsWithoutWritingAFile()
    {
        var (exit, _, err) = await Cli("restore", "abandon abandon abandon", "--no-encryption", "--file", WalletFile());
        Assert.Equal(1, exit);
        Assert.Contains("Invalid mnemonic", err);
        Assert.False(File.Exists(WalletFile()));
    }

    [Fact]
    public async Task Restore_Encrypts_AndInfoNeedsThePassword()
    {
        var (exit, _, _) = await Cli("restore", Mnemonic, "--net", "regtest", "--password", "pw", "--file", WalletFile());
        Assert.Equal(0, exit);
        Assert.True(EncryptedFile.IsEncrypted(File.ReadAllText(WalletFile())));
        Assert.DoesNotContain("abandon", File.ReadAllText(WalletFile()));

        var (okExit, info, _) = await Cli("info", "--net", "regtest", "--password", "pw", "--file", WalletFile());
        Assert.Equal(0, okExit);
        Assert.Contains("watch-only: False", info);

        var (badExit, _, err) = await Cli("info", "--net", "regtest", "--password", "wrong", "--file", WalletFile());
        Assert.Equal(1, badExit);
        Assert.Contains("Error:", err);
    }

    [Fact]
    public async Task Create_ShowsTheMnemonicOnce_AndWritesTheWallet()
    {
        var (exit, output, _) = await Cli("create", "--net", "regtest", "--no-encryption", "--file", WalletFile());
        Assert.Equal(0, exit);
        Assert.Contains("New mnemonic", output);
        Assert.True(File.Exists(WalletFile()));
    }

    [Fact]
    public async Task RestoreXpub_AndRestoreAddress_ProduceWatchOnlyWallets()
    {
        Assert.True(Bip39.TryParse(Mnemonic, out var m));
        var account = HdAccount.FromMnemonic(m!, null, ScriptKind.NativeSegwit, ChainProfiles.Regtest);

        var (e1, _, _) = await Cli("restore-xpub", account.ToSlip132(), "--net", "regtest", "--no-encryption", "--file", WalletFile("x.json"));
        Assert.Equal(0, e1);
        Assert.Contains("watch-only: True", (await Cli("info", "--net", "regtest", "--file", WalletFile("x.json"))).Out);

        var (e2, _, _) = await Cli("restore-address", account.GetReceiveAddress(0).ToString(), "--net", "regtest", "--no-encryption", "--file", WalletFile("a.json"));
        Assert.Equal(0, e2);
        Assert.Contains("watch-only: True", (await Cli("info", "--net", "regtest", "--file", WalletFile("a.json"))).Out);

        var (e3, _, err3) = await Cli("restore-xpub", "garbage", "--net", "regtest", "--no-encryption", "--file", WalletFile("bad.json"));
        Assert.Equal(1, e3);
        Assert.Contains("not recognised", err3);
    }

    [Fact]
    public async Task Send_SynchronizesFirst_SoItNeverBuildsFromAnEarlierCache()
    {
        var account = HdAccount.FromMnemonic(
            Bip39.TryParse(Mnemonic, out var m) ? m! : throw new InvalidOperationException(),
            null, ScriptKind.NativeSegwit, ChainProfiles.Regtest);
        await using var server = new FakeElectrumServer();
        server.Handle("server.features", _ => new { genesis_hash = ChainProfiles.Regtest.GenesisHash });
        server.Handle("blockchain.headers.subscribe", _ => new { height = 300, hex = RegtestHeader(300) });
        server.Handle("blockchain.scripthash.subscribe", _ => null);
        server.Handle("blockchain.scripthash.get_history", _ => Array.Empty<object>());

        await Cli("restore", Mnemonic, "--net", "regtest", "--no-encryption", "--file", WalletFile());
        var (exit, _, err) = await Cli("send", "--to", account.GetReceiveAddress(9).ToString(), "--all",
            "--server", $"{server.Host}:{server.Port}", "--net", "regtest", "--file", WalletFile());

        Assert.Equal(1, exit);
        Assert.Contains("No spendable UTXOs", err);                 // empty wallet, now known from the server
        Assert.True(server.CallCount("blockchain.scripthash.get_history") > 0); // the send did sync
    }

    [Fact]
    public async Task Send_ToAnAddressOfAnotherNetwork_FailsWithACleanError()
    {
        await Cli("restore", Mnemonic, "--net", "regtest", "--no-encryption", "--file", WalletFile());
        var mainnetAddress = "plm1qdq3gu2zvg9lyr8gxd6yln4wavc5tlp8prmvfay"; // Palladium mainnet
        var (exit, _, err) = await Cli("send", "--to", mainnetAddress, "--amount", "1",
            "--net", "regtest", "--file", WalletFile());

        Assert.Equal(1, exit);
        Assert.Contains("not a valid address for regtest", err);
        Assert.DoesNotContain("   at ", err); // no stack trace
    }

    private static readonly string WrongTxid = new('b', 64);

    /// <summary>Wires <paramref name="server"/> so that the wallet of <see cref="Mnemonic"/> holds one 0.1 PLM coin.</summary>
    private static (HdAccount Account, Transaction Funding) WireFundedServer(FakeElectrumServer server)
    {
        var net = PalladiumNetworks.Regtest;
        Assert.True(Bip39.TryParse(Mnemonic, out var mnemonic));
        var account = HdAccount.FromMnemonic(mnemonic!, null, ScriptKind.NativeSegwit, ChainProfiles.Regtest);
        var funding = net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(10_000_000), account.GetReceiveAddress(0));
        var scripthash = Scripthash.FromAddress(account.GetReceiveAddress(0));

        server.Handle("server.features", _ => new { genesis_hash = ChainProfiles.Regtest.GenesisHash });
        server.Handle("blockchain.headers.subscribe", _ => new { height = 300, hex = RegtestHeader(300) });
        server.Handle("blockchain.scripthash.subscribe", _ => null);
        server.Handle("blockchain.scripthash.get_history", p =>
            p[0].GetString() == scripthash
                ? new[] { new { tx_hash = funding.GetHash().ToString(), height = 100 } }
                : Array.Empty<object>());
        server.Handle("blockchain.transaction.get", _ => funding.ToHex());
        server.Handle("blockchain.transaction.get_merkle",
            _ => new { block_height = 100, pos = 0, merkle = Array.Empty<string>() });
        server.Handle("blockchain.block.header", _ => RegtestHeader(100, funding.GetHash()));
        return (account, funding);
    }

    [Fact]
    public async Task Broadcast_WithAWrongTxidReply_AndTheTransactionNotInTheNetwork_PrintsTheSignedHex_AndFails()
    {
        await using var server = new FakeElectrumServer();
        var (account, funding) = WireFundedServer(server);
        server.Handle("blockchain.transaction.broadcast", _ => WrongTxid);
        server.Handle("blockchain.transaction.get", p =>
            p[0].GetString() == funding.GetHash().ToString()
                ? funding.ToHex()
                : throw new FakeElectrumError(2, "No such mempool or blockchain transaction"));
        await Cli("restore", Mnemonic, "--net", "regtest", "--no-encryption", "--file", WalletFile());

        var (exit, output, err) = await Cli("send", "--to", account.GetReceiveAddress(9).ToString(), "--amount", "0.05",
            "--broadcast", "--server", $"{server.Host}:{server.Port}", "--net", "regtest", "--file", WalletFile());

        Assert.Equal(1, exit);
        Assert.Contains("is not in the network", err);
        var hex = output.Split('\n', StringSplitOptions.TrimEntries).Last(l => l.Length > 100 && l.All(Uri.IsHexDigit));
        var tx = Transaction.Parse(hex, PalladiumNetworks.Regtest);
        Assert.Contains(tx.Outputs, o => o.Value == Money.Satoshis(5_000_000));
        Assert.Contains(tx.GetHash().ToString(), err);
    }

    [Fact]
    public async Task Broadcast_WithAWrongTxidReply_AndTheTransactionInTheNetwork_SucceedsAndReportsTheServer()
    {
        await using var server = new FakeElectrumServer();
        var (account, funding) = WireFundedServer(server);
        string? sent = null;
        server.Handle("blockchain.transaction.broadcast", p => { sent = p[0].GetString(); return WrongTxid; });
        server.Handle("blockchain.transaction.get", p =>
            sent is not null && Transaction.Parse(sent, PalladiumNetworks.Regtest).GetHash().ToString() == p[0].GetString() ? sent
            : p[0].GetString() == funding.GetHash().ToString() ? funding.ToHex()
            : throw new FakeElectrumError(2, "No such mempool or blockchain transaction"));
        await Cli("restore", Mnemonic, "--net", "regtest", "--no-encryption", "--file", WalletFile());

        var (exit, output, _) = await Cli("send", "--to", account.GetReceiveAddress(9).ToString(), "--amount", "0.05",
            "--broadcast", "--server", $"{server.Host}:{server.Port}", "--net", "regtest", "--file", WalletFile());

        Assert.Equal(0, exit);
        var ourTxid = Transaction.Parse(sent!, PalladiumNetworks.Regtest).GetHash().ToString();
        Assert.Contains($"{ourTxid} is in the network", output);
        Assert.Contains(WrongTxid, output);
    }

    /// <summary>A regtest block header with the given height, enough for the synchronizer's checks.</summary>
    private static string RegtestHeader(int height, uint256? merkleRoot = null)
    {
        var header = PalladiumNetworks.Regtest.Consensus.ConsensusFactory.CreateBlockHeader();
        header.HashPrevBlock = uint256.Zero;
        header.HashMerkleRoot = merkleRoot ?? uint256.One;
        header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + height);
        header.Bits = new Target(0x1d00ffffu);
        header.Nonce = (uint)height;
        return Convert.ToHexString(header.ToBytes()).ToLowerInvariant();
    }

    [Fact]
    public async Task Sync_ThenSend_AgainstAFakeServer_ProducesASignedTransaction()
    {
        var net = PalladiumNetworks.Regtest;
        Assert.True(Bip39.TryParse(Mnemonic, out var mnemonic));
        var account = HdAccount.FromMnemonic(mnemonic!, null, ScriptKind.NativeSegwit, ChainProfiles.Regtest);
        var funding = net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(10_000_000), account.GetReceiveAddress(0));
        var txid = funding.GetHash();
        var scripthash = Scripthash.FromAddress(account.GetReceiveAddress(0));

        string Header(uint256 root, int h)
        {
            var header = net.Consensus.ConsensusFactory.CreateBlockHeader();
            header.HashPrevBlock = uint256.Zero;
            header.HashMerkleRoot = root;
            header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + h);
            header.Bits = new Target(0x1d00ffffu);
            header.Nonce = (uint)h;
            return Convert.ToHexString(header.ToBytes()).ToLowerInvariant();
        }

        await using var server = new FakeElectrumServer();
        server.Handle("server.features", _ => new { genesis_hash = ChainProfiles.Regtest.GenesisHash });
        server.Handle("blockchain.headers.subscribe", _ => new { height = 300, hex = Header(uint256.One, 300) });
        server.Handle("blockchain.scripthash.subscribe", _ => null);
        server.Handle("blockchain.scripthash.get_history", p =>
            p[0].GetString() == scripthash ? new[] { new { tx_hash = txid.ToString(), height = 100 } } : Array.Empty<object>());
        server.Handle("blockchain.transaction.get", _ => funding.ToHex());
        server.Handle("blockchain.transaction.get_merkle", _ => new { block_height = 100, pos = 0, merkle = Array.Empty<string>() });
        server.Handle("blockchain.block.header", _ => Header(txid, 100));

        await Cli("restore", Mnemonic, "--net", "regtest", "--no-encryption", "--file", WalletFile());
        var serverArg = $"{server.Host}:{server.Port}";

        var (syncExit, syncOut, _) = await Cli("sync", "--server", serverArg, "--net", "regtest", "--file", WalletFile());
        Assert.Equal(0, syncExit);
        Assert.Contains("0.1", syncOut);                       // 10,000,000 sat
        Assert.Contains(txid.ToString(), syncOut);
        Assert.DoesNotContain("(unverified)", syncOut);

        var dest = account.GetReceiveAddress(9).ToString();
        var (sendExit, sendOut, _) = await Cli("send", "--to", dest, "--amount", "0.05", "--feerate", "2",
            "--server", serverArg, "--net", "regtest", "--file", WalletFile());
        Assert.Equal(0, sendExit);
        Assert.Contains("NOT broadcast", sendOut);
        var hex = sendOut.Split('\n', StringSplitOptions.TrimEntries).Last(l => l.Length > 100 && l.All(Uri.IsHexDigit));
        var tx = Transaction.Parse(hex, net);
        Assert.Contains(tx.Outputs, o => o.Value == Money.Satoshis(5_000_000) && o.ScriptPubKey == account.GetReceiveAddress(9).ScriptPubKey);
        Assert.Equal(txid, tx.Inputs[0].PrevOut.Hash);
        Assert.Equal(0, server.CallCount("blockchain.transaction.broadcast")); // nothing broadcast without --broadcast
    }

    [Fact]
    public async Task Sync_refuses_a_server_on_another_chain()
    {
        await using var server = new FakeElectrumServer(); // default features: mainnet genesis
        await Cli("restore", Mnemonic, "--net", "regtest", "--no-encryption", "--file", WalletFile());

        var (exit, _, err) = await Cli("sync", "--server", $"{server.Host}:{server.Port}",
            "--net", "regtest", "--file", WalletFile());

        Assert.Equal(1, exit);
        Assert.Contains("not on the expected chain", err);
        Assert.Equal(0, server.CallCount("blockchain.headers.subscribe")); // rejected before syncing
    }

    // ---- wallet password handling (ADR 0038) ----

    [Fact]
    public async Task Create_WithoutAPasswordOrNoEncryption_FailsInsteadOfStoringPlaintext()
    {
        var (exit, _, err) = await Cli("restore", Mnemonic, "--net", "regtest", "--file", WalletFile());

        Assert.Equal(1, exit);
        Assert.Contains("A password is required", err);
        Assert.False(File.Exists(WalletFile()));
    }

    [Fact]
    public async Task NoEncryption_StoresPlaintextAndWarns()
    {
        var (exit, _, err) = await Cli("restore", Mnemonic, "--net", "regtest", "--no-encryption", "--file", WalletFile());

        Assert.Equal(0, exit);
        Assert.Contains("unencrypted", err);
        Assert.False(EncryptedFile.IsEncrypted(File.ReadAllText(WalletFile())));
    }

    [Fact]
    public async Task NoEncryption_CannotBeCombinedWithAPassword()
    {
        var (exit, _, err) = await Cli("restore", Mnemonic, "--net", "regtest", "--no-encryption",
            "--password-stdin", "--file", WalletFile());

        Assert.Equal(1, exit);
        Assert.Contains("cannot be combined", err);
        Assert.False(File.Exists(WalletFile()));
    }

    [Fact]
    public async Task PasswordStdin_EncryptsAndThenOpensTheWallet()
    {
        var (e1, _, _) = await CliWithStdin("s3cret\n", "restore", Mnemonic, "--net", "regtest",
            "--password-stdin", "--file", WalletFile());
        Assert.Equal(0, e1);
        var text = File.ReadAllText(WalletFile());
        Assert.True(EncryptedFile.IsEncrypted(text));
        Assert.DoesNotContain("abandon", text);

        var (e2, info, _) = await CliWithStdin("s3cret\n", "info", "--net", "regtest",
            "--password-stdin", "--file", WalletFile());
        Assert.Equal(0, e2);
        Assert.Contains("network:  regtest", info);
    }

    [Fact]
    public async Task PasswordEnv_EncryptsAndThenOpensTheWallet()
    {
        const string name = "PLM_TEST_WALLET_PW";
        Environment.SetEnvironmentVariable(name, "from-env");
        try
        {
            var (e1, _, err1) = await Cli("restore", Mnemonic, "--net", "regtest", "--password-env", name,
                "--file", WalletFile());
            Assert.Equal(0, e1);
            Assert.DoesNotContain("--password is visible", err1);   // no warning for the safe route
            Assert.True(EncryptedFile.IsEncrypted(File.ReadAllText(WalletFile())));

            var (e2, _, _) = await Cli("info", "--net", "regtest", "--password-env", name, "--file", WalletFile());
            Assert.Equal(0, e2);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public async Task PasswordEnv_UnsetOrEmpty_IsAnError()
    {
        var (exit, _, err) = await Cli("restore", Mnemonic, "--net", "regtest",
            "--password-env", "PLM_TEST_SURELY_UNSET", "--file", WalletFile());

        Assert.Equal(1, exit);
        Assert.Contains("PLM_TEST_SURELY_UNSET", err);
    }

    [Fact]
    public async Task EncryptedWallet_WithNoPasswordAndNoTerminal_FailsWithAClearError()
    {
        await CliWithStdin("pw\n", "restore", Mnemonic, "--net", "regtest", "--password-stdin", "--file", WalletFile());

        var (exit, _, err) = await Cli("info", "--net", "regtest", "--file", WalletFile()); // no TTY: never prompts or hangs

        Assert.Equal(1, exit);
        Assert.Contains("encrypted", err);
        Assert.Contains("--password-env", err);
    }

    [Fact]
    public async Task EncryptedWallet_WithTheWrongPasswordFails()
    {
        await CliWithStdin("pw\n", "restore", Mnemonic, "--net", "regtest", "--password-stdin", "--file", WalletFile());

        var (exit, _, err) = await CliWithStdin("nope\n", "info", "--net", "regtest", "--password-stdin", "--file", WalletFile());

        Assert.Equal(1, exit);
        Assert.NotEmpty(err);
    }

    [Fact]
    public async Task PasswordFlag_StillWorksButWarnsAndNeverEchoesThePassword()
    {
        var (exit, output, err) = await Cli("restore", Mnemonic, "--net", "regtest", "--password", "hunter2",
            "--file", WalletFile());

        Assert.Equal(0, exit);
        Assert.Contains("--password is visible", err);
        Assert.DoesNotContain("hunter2", output + err);
        Assert.True(EncryptedFile.IsEncrypted(File.ReadAllText(WalletFile())));
    }

    [Fact]
    public async Task MoreThanOnePasswordSource_IsRejected()
    {
        var (exit, _, err) = await Cli("restore", Mnemonic, "--net", "regtest", "--password", "a",
            "--password-stdin", "--file", WalletFile());

        Assert.Equal(1, exit);
        Assert.Contains("only one", err);
    }

    [Fact]
    public async Task Sync_OfAnEncryptedWallet_SavesItBackEncrypted()
    {
        await CliWithStdin("pw\n", "restore", Mnemonic, "--net", "regtest", "--password-stdin", "--file", WalletFile());
        await using var server = new FakeElectrumServer();
        server.Handle("server.features", _ => new { genesis_hash = ChainProfiles.Regtest.GenesisHash });
        server.Handle("blockchain.headers.subscribe", _ => new { height = 5, hex = new string('0', 160) });
        server.Handle("blockchain.scripthash.subscribe", _ => null);
        server.Handle("blockchain.scripthash.get_history", _ => Array.Empty<object>());

        var (exit, _, _) = await CliWithStdin("pw\n", "sync", "--server", $"{server.Host}:{server.Port}",
            "--net", "regtest", "--password-stdin", "--file", WalletFile());

        Assert.Equal(0, exit);
        Assert.True(EncryptedFile.IsEncrypted(File.ReadAllText(WalletFile())));
    }

    // ----- coin listing and freezing (offline) -----

    private static readonly string CoinTxid = new('a', 64);

    private async Task<string> WalletWithOneCoin()
    {
        var file = WalletFile();
        await Cli("restore", Mnemonic, "--net", "regtest", "--no-encryption", "--file", file);
        var doc = WalletStore.Load(file, null);
        doc.Cache = new SyncCache
        {
            TipHeight = 200,
            SpendableSats = 50_000,
            Utxos = [new CachedUtxo
            {
                Txid = CoinTxid, Vout = 1, ValueSats = 50_000, Address = "rplm1qexample",
                Height = 150, Verified = true,
            }],
        };
        WalletStore.Save(doc, file, null);
        return file;
    }

    [Fact]
    public async Task Utxos_ListsTheCachedCoins_AndNeedsASyncFirst()
    {
        var file = WalletFile();
        await Cli("restore", Mnemonic, "--net", "regtest", "--no-encryption", "--file", file);
        var (neverSynced, _, err) = await Cli("utxos", "--net", "regtest", "--file", file);
        Assert.Equal(1, neverSynced);
        Assert.Contains("never been synchronized", err);

        await WalletWithOneCoin();
        var (exit, output, _) = await Cli("utxos", "--net", "regtest", "--file", file);
        Assert.Equal(0, exit);
        Assert.Contains($"{CoinTxid}:1", output);
        Assert.Contains("spendable", output);
    }

    [Fact]
    public async Task Freeze_KeepsTheCoinOutOfTheBalance_AndUnfreezeRestoresIt()
    {
        var file = await WalletWithOneCoin();

        var (exit, output, _) = await Cli("freeze", $"{CoinTxid}:1", "--net", "regtest", "--file", file);
        Assert.Equal(0, exit);
        Assert.Contains("Frozen", output);
        var frozen = WalletStore.Load(file, null).Cache!;
        Assert.True(frozen.Utxos[0].Frozen);
        Assert.Equal(0, frozen.SpendableSats);
        Assert.Contains("frozen", (await Cli("utxos", "--net", "regtest", "--file", file)).Out);

        await Cli("unfreeze", $"{CoinTxid}:1", "--net", "regtest", "--file", file);
        var restored = WalletStore.Load(file, null).Cache!;
        Assert.False(restored.Utxos[0].Frozen);
        Assert.Equal(50_000, restored.SpendableSats);
    }

    [Theory]
    [InlineData("nothex:1")]
    [InlineData("aaaa")]
    [InlineData("TXID")]
    public async Task Freeze_WithAMalformedOutpoint_FailsCleanly(string outpoint)
    {
        var file = await WalletWithOneCoin();
        var (exit, _, err) = await Cli("freeze", outpoint, "--net", "regtest", "--file", file);
        Assert.Equal(1, exit);
        Assert.Contains("<txid>:<vout>", err);
    }

    [Fact]
    public async Task Freeze_OfACoinTheWalletDoesNotHave_FailsAndChangesNothing()
    {
        var file = await WalletWithOneCoin();
        var (exit, _, err) = await Cli("freeze", $"{new string('b', 64)}:0", "--net", "regtest", "--file", file);
        Assert.Equal(1, exit);
        Assert.Contains("not a coin of this wallet", err);
        Assert.False(WalletStore.Load(file, null).Cache!.Utxos[0].Frozen);
    }

    // ----- malformed input and environment failures are clean errors, never a stack trace -----

    [Theory]
    [InlineData("restore", "--path", "foo")]
    [InlineData("addresses", "--path", "foo")]
    public async Task AnInvalidDerivationPath_IsACleanError(string command, string flag, string value)
    {
        var (exit, _, err) = await Cli(command, Mnemonic, "--net", "regtest", "--no-encryption", flag, value,
            "--file", WalletFile());
        Assert.Equal(1, exit);
        Assert.Contains("--path is not a valid derivation path", err);
        Assert.False(File.Exists(WalletFile()));
    }

    [Theory]
    [InlineData("127.0.0.1:abc")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:70000")]
    public async Task AnInvalidServerPort_IsACleanError(string server)
    {
        var file = await WalletWithOneCoin();
        var (exit, _, err) = await Cli("sync", "--server", server, "--net", "regtest", "--file", file);
        Assert.Equal(1, exit);
        Assert.Contains("port in 1-65535", err);
    }

    [Fact]
    public async Task AServerThatRefusesTheConnection_IsACleanError()
    {
        var file = await WalletWithOneCoin();
        int freePort;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            freePort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        } // closed again: nothing listens on it
        var (exit, _, err) = await Cli("sync", "--server", $"127.0.0.1:{freePort}", "--net", "regtest", "--file", file);
        Assert.Equal(1, exit);
        Assert.StartsWith("Error:", err);
        Assert.DoesNotContain("   at ", err);
    }

    [Fact]
    public async Task ACorruptWalletFile_IsACleanError()
    {
        var file = WalletFile();
        await File.WriteAllTextAsync(file, "garbage");
        var (exit, _, err) = await Cli("info", "--net", "regtest", "--file", file);
        Assert.Equal(1, exit);
        Assert.Contains("not a valid wallet file", err);
    }

    [Fact]
    public async Task SignPsbt_WithAMalformedScan_IsACleanError()
    {
        // A real, parseable PSBT is needed to reach the --scan parsing.
        var file = await WalletWithOneCoin();
        var net = PalladiumNetworks.Regtest;
        var tx = net.CreateTransaction();
        tx.Inputs.Add(new NBitcoin.OutPoint(NBitcoin.uint256.One, 0));
        tx.Outputs.Add(NBitcoin.Money.Satoshis(1000), new NBitcoin.Key().PubKey.WitHash);
        var psbt = NBitcoin.PSBT.FromTransaction(tx, net).ToBase64();
        var (exit, _, err) = await Cli("sign-psbt", psbt, "--scan", "abc", "--net", "regtest", "--file", file);
        Assert.Equal(1, exit);
        Assert.Contains("--scan must be", err);
    }

    // ----- WIF import and taproot (ADR 0053) -----

    private static string NewWif() => new Key().GetBitcoinSecret(PalladiumNetworks.Regtest).ToString();

    [Fact]
    public async Task Create_WithKindTaproot_ProducesABech32mWalletThatInfoReads()
    {
        var (exit, output, _) = await Cli("create", "--kind", "taproot", "--net", "regtest", "--no-encryption",
            "--file", WalletFile());
        Assert.Equal(0, exit);
        Assert.Contains("First address: rplm1p", output); // witness v1: bech32m, 'p'
        var (_, info, _) = await Cli("info", "--net", "regtest", "--file", WalletFile());
        Assert.Contains("kind: Taproot", info);
        Assert.Contains("m/86'", info); // BIP86
    }

    [Fact]
    public async Task AnUnknownKind_IsRejected_NeverDefaultedToSegwit()
    {
        var (exit, _, err) = await Cli("create", "--kind", "taprot", "--net", "regtest", "--no-encryption",
            "--file", WalletFile());
        Assert.Equal(1, exit);
        Assert.Contains("is not one of segwit, wrapped, legacy, taproot", err);
        Assert.False(File.Exists(WalletFile()));
    }

    [Theory]
    [InlineData("segwit", "rplm1q")]
    [InlineData("taproot", "rplm1p")]
    [InlineData("legacy", "")]
    public async Task RestoreWif_ImportsASpendableWalletOfTheRequestedKind(string kind, string prefix)
    {
        var wif = NewWif();
        var (exit, output, _) = await Cli("restore-wif", wif, "--kind", kind, "--net", "regtest", "--no-encryption",
            "--file", WalletFile());
        Assert.Equal(0, exit);
        Assert.Contains("1 key(s)", output);
        var doc = WalletStore.Load(WalletFile(), null);
        Assert.False(doc.IsWatchOnly);
        Assert.Equal([wif], doc.WifKeys);
        var (_, info, _) = await Cli("info", "--net", "regtest", "--file", WalletFile());
        Assert.Contains("imported: 1 private key(s)", info);
        Assert.Contains($"receive:  {prefix}", info);
        Assert.DoesNotContain("xpub:", info);
    }

    [Fact]
    public async Task RestoreWif_ReadsTheKeysFromStdin_AndCollapsesDuplicates()
    {
        var wif = NewWif();
        var (exit, output, _) = await CliWithStdin($"{wif}\n{NewWif()}\n{wif}\n", "restore-wif", "-",
            "--net", "regtest", "--no-encryption", "--file", WalletFile());
        Assert.Equal(0, exit);
        Assert.Contains("2 key(s)", output);
    }

    [Fact]
    public async Task RestoreWif_WithAnInvalidKeyOrAnotherNetwork_FailsWithoutWritingAFile()
    {
        var (exit, _, err) = await Cli("restore-wif", "notakey", "--net", "regtest", "--no-encryption", "--file", WalletFile());
        Assert.Equal(1, exit);
        Assert.Contains("Invalid WIF key", err);

        var mainnetWif = new Key().GetBitcoinSecret(PalladiumNetworks.Mainnet).ToString();
        (exit, _, err) = await Cli("restore-wif", mainnetWif, "--net", "regtest", "--no-encryption", "--file", WalletFile());
        Assert.Equal(1, exit);
        Assert.Contains("Invalid WIF key", err);
        Assert.False(File.Exists(WalletFile()));
    }

    [Fact]
    public async Task RestoreWif_Encrypts_WhenAPasswordIsGiven()
    {
        await CliWithStdin("pw\n", "restore-wif", NewWif(), "--net", "regtest", "--password-stdin", "--file", WalletFile());
        Assert.True(EncryptedFile.IsEncrypted(File.ReadAllText(WalletFile())));
    }
}
