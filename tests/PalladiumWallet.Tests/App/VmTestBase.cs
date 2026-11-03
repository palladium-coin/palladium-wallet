using Avalonia.Threading;
using NBitcoin;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;
using PalladiumWallet.Tests.Net;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// Headless tests of the single application ViewModel. Everything is sandboxed: the data
/// root is a temp folder, the saved server points at an in-process FakeElectrumServer (so the
/// auto-connect never reaches a real server) and the GitHub update check is stubbed.
/// </summary>
[Collection("AppPaths")] // static AppPaths / Loc / update-check seam
public abstract class VmTestBase : IDisposable
{
    protected const string Mnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"plm-vm-{Guid.NewGuid()}");
    private readonly string? _savedOverride = AppPaths.OverrideDataRoot;
    private readonly Func<string, Task<LatestRelease?>> _savedUpdateCheck = MainWindowViewModel.UpdateCheck;
    protected readonly FakeElectrumServer _server = new();

    protected VmTestBase()
    {
        AppPaths.OverrideDataRoot = _root;
        MainWindowViewModel.UpdateCheck = _ => Task.FromResult<LatestRelease?>(null);
        // No bootstrap servers: the only reachable server is the in-process fake, so the
        // VM's connect/reconnect loops can never touch a real indexing server.
        // No checkpoints either: the fake chains use tiny heights, and with the real mainnet checkpoints
        // every proof would have to be anchored up to height 20,000 (ADR 0040), which the fake
        // server does not serve. Tests that care about anchoring set their own profile.
        MainWindowViewModel.ProfileOverride = ChainProfiles.Mainnet with { BootstrapServers = [], Checkpoints = [] };
        new AppConfig { LastServerHost = _server.Host, LastServerPort = _server.Port, LastServerUseSsl = false }
            .Save(AppPaths.ConfigPath());
    }

    public void Dispose()
    {
        MainWindowViewModel.UpdateCheck = _savedUpdateCheck;
        // ProfileOverride is intentionally left in place: ViewModels created by a test keep their
        // keep-alive timers running, and restoring the real profile would let them reconnect to
        // the real bootstrap servers after the test is over. Nothing else in the suite uses it.
        AppPaths.OverrideDataRoot = _savedOverride;
        Loc.SwitchTo("en");
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    protected async Task Eventually(Func<bool> condition, int timeoutMs = 10_000, Func<string>? describe = null)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            Assert.True(waited < timeoutMs, "condition not reached in time" + (describe is null ? "" : $": {describe()}") + $" | calls: {Calls()}");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private string Calls() => string.Join(", ", new[]
    {
        "blockchain.headers.subscribe", "blockchain.scripthash.get_history", "blockchain.transaction.get",
        "blockchain.transaction.get_merkle", "blockchain.block.header", "blockchain.block.headers",
    }.Select(m => $"{m.Split('.')[^1]}={_server.CallCount(m)}"));

    protected static void RestoreFlow(MainWindowViewModel vm, string mnemonic, string? password, string? name = null)
    {
        vm.WizardStartRestoreCommand.Execute(null);
        vm.MnemonicInput = mnemonic;
        vm.WizardNextFromWordsCommand.Execute(null);
        vm.WizardNextFromPassphraseCommand.Execute(null);
        vm.WizardNextFromScriptTypeCommand.Execute(null);
        vm.EncryptWallet = password is not null;
        vm.PasswordInput = vm.ConfirmPasswordInput = password ?? "";
        vm.WalletNameInput = name ?? "";
        vm.CreateOrRestoreCommand.Execute(null);
    }

    protected static Mnemonic ParseMnemonic()
    {
        Assert.True(Bip39.TryParse(Mnemonic, out var m));
        return m!;
    }

    protected (Transaction Funding, string TxHex) WireFundedChain(BitcoinAddress to, long sats)
    {
        var net = PalladiumNetworks.Mainnet;
        var tx = net.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(Money.Satoshis(sats), to);
        var txid = tx.GetHash();
        var scripthash = Scripthash.FromAddress(to);

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

        _server.Handle("blockchain.headers.subscribe", _ => new { height = 200, hex = Header(uint256.One, 200) });
        _server.Handle("blockchain.scripthash.subscribe", _ => null);
        _server.Handle("blockchain.scripthash.get_history", p =>
            p[0].GetString() == scripthash ? new[] { new { tx_hash = txid.ToString(), height = 100 } } : Array.Empty<object>());
        _server.Handle("blockchain.transaction.get", _ => tx.ToHex());
        _server.Handle("blockchain.transaction.get_merkle",
            _ => new { block_height = 100, pos = 0, merkle = Array.Empty<string>() });
        _server.Handle("blockchain.block.header", _ => Header(txid, 100));
        _server.Handle("blockchain.block.headers", _ => new { count = 0, hex = "" });
        return (tx, tx.ToHex());
    }
}
