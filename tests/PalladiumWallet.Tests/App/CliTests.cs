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

    private static async Task<(int Exit, string Out, string Err)> Cli(params string[] args)
    {
        var entry = Assembly.Load("PalladiumWallet.Cli").EntryPoint
            ?? throw new InvalidOperationException("CLI has no entry point.");
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        var (o, e) = (new StringWriter(), new StringWriter());
        Console.SetOut(o); Console.SetError(e);
        try
        {
            // The top-level-statements entry point is a synchronous Main(string[]) → int wrapper.
            var exit = await Task.Run(() => (int)entry.Invoke(null, [args])!);
            return (exit, o.ToString(), e.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
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
        var (exit, _, err) = await Cli("restore", "abandon abandon abandon", "--file", WalletFile());
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
        var (exit, output, _) = await Cli("create", "--net", "regtest", "--file", WalletFile());
        Assert.Equal(0, exit);
        Assert.Contains("New mnemonic", output);
        Assert.True(File.Exists(WalletFile()));
    }

    [Fact]
    public async Task RestoreXpub_AndRestoreAddress_ProduceWatchOnlyWallets()
    {
        Assert.True(Bip39.TryParse(Mnemonic, out var m));
        var account = HdAccount.FromMnemonic(m!, null, ScriptKind.NativeSegwit, ChainProfiles.Regtest);

        var (e1, _, _) = await Cli("restore-xpub", account.ToSlip132(), "--net", "regtest", "--file", WalletFile("x.json"));
        Assert.Equal(0, e1);
        Assert.Contains("watch-only: True", (await Cli("info", "--net", "regtest", "--file", WalletFile("x.json"))).Out);

        var (e2, _, _) = await Cli("restore-address", account.GetReceiveAddress(0).ToString(), "--net", "regtest", "--file", WalletFile("a.json"));
        Assert.Equal(0, e2);
        Assert.Contains("watch-only: True", (await Cli("info", "--net", "regtest", "--file", WalletFile("a.json"))).Out);

        var (e3, _, err3) = await Cli("restore-xpub", "garbage", "--net", "regtest", "--file", WalletFile("bad.json"));
        Assert.Equal(1, e3);
        Assert.Contains("not recognised", err3);
    }

    [Fact]
    public async Task Send_BeforeAnySync_AsksToSyncFirst()
    {
        await Cli("restore", Mnemonic, "--net", "regtest", "--file", WalletFile());
        var (exit, _, err) = await Cli("send", "--to", "x", "--all", "--net", "regtest", "--file", WalletFile());
        Assert.Equal(1, exit);
        Assert.Contains("run 'sync' first", err);
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

        await Cli("restore", Mnemonic, "--net", "regtest", "--file", WalletFile());
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
        await Cli("restore", Mnemonic, "--net", "regtest", "--file", WalletFile());

        var (exit, _, err) = await Cli("sync", "--server", $"{server.Host}:{server.Port}",
            "--net", "regtest", "--file", WalletFile());

        Assert.Equal(1, exit);
        Assert.Contains("not on the expected chain", err);
        Assert.Equal(0, server.CallCount("blockchain.headers.subscribe")); // rejected before syncing
    }
}
