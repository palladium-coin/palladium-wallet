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

    /// <summary>A regtest block header with the given height, enough for the synchronizer's checks.</summary>
    private static string RegtestHeader(int height)
    {
        var header = PalladiumNetworks.Regtest.Consensus.ConsensusFactory.CreateBlockHeader();
        header.HashPrevBlock = uint256.Zero;
        header.HashMerkleRoot = uint256.One;
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
}
