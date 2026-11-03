using NBitcoin.Crypto;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Tests.Net;

namespace PalladiumWallet.Tests.Spv;

/// <summary>Chain-identity check after the handshake (genesis + highest checkpoint ≤ tip).</summary>
public class ChainVerifierTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // 80 arbitrary bytes are a valid "header" for hashing purposes; seed makes them distinct.
    private static string HeaderHex(int seed) =>
        Convert.ToHexString(Hashes.SHA256(BitConverter.GetBytes(seed))
            .Concat(Hashes.SHA256(BitConverter.GetBytes(seed + 1_000)))
            .Concat(new byte[16]).ToArray()).ToLowerInvariant();

    private static Checkpoint CheckpointFor(int height, int seed) =>
        new(height, BlockHeaderInfo.Parse(HeaderHex(seed)).Hash.ToString(), 0x1d00ffff);

    private static ChainProfile Profile(params Checkpoint[] checkpoints) =>
        ChainProfiles.Mainnet with { Checkpoints = checkpoints };

    private static async Task<ElectrumClient> ConnectAsync(FakeElectrumServer s) =>
        await ElectrumClient.ConnectAsync(s.Host, s.Port, useSsl: false);

    private static void Tip(FakeElectrumServer s, int height) =>
        s.Handle("blockchain.headers.subscribe", _ => new { height, hex = HeaderHex(0) });

    [Fact]
    public async Task Accepts_a_server_with_the_expected_genesis_and_checkpoint_header()
    {
        await using var server = new FakeElectrumServer();
        Tip(server, 150);
        server.Handle("blockchain.block.header", p => p[0].GetInt32() == 100 ? HeaderHex(7) : throw new FakeElectrumError(-1, "no"));
        await using var client = await ConnectAsync(server);

        await client.VerifyChainAsync(Profile(CheckpointFor(100, 7)), default).WaitAsync(Timeout);
    }

    [Fact]
    public async Task Uses_the_highest_checkpoint_not_above_the_tip()
    {
        await using var server = new FakeElectrumServer();
        Tip(server, 150);
        var asked = new List<int>();
        server.Handle("blockchain.block.header", p => { asked.Add(p[0].GetInt32()); return HeaderHex(asked[^1]); });
        await using var client = await ConnectAsync(server);

        await client.VerifyChainAsync(
            Profile(CheckpointFor(50, 50), CheckpointFor(100, 100), CheckpointFor(200, 200)), default).WaitAsync(Timeout);

        Assert.Equal([100], asked);
    }

    [Fact]
    public async Task Rejects_a_different_header_at_the_checkpoint_height()
    {
        await using var server = new FakeElectrumServer();
        Tip(server, 150);
        server.Handle("blockchain.block.header", _ => HeaderHex(999)); // other chain
        await using var client = await ConnectAsync(server);

        var ex = await Assert.ThrowsAsync<WrongChainException>(
            () => client.VerifyChainAsync(Profile(CheckpointFor(100, 7)), default).WaitAsync(Timeout));
        Assert.Equal(server.Port, ex.Port);
        Assert.Contains("checkpoint", ex.Message);
    }

    [Fact]
    public async Task Rejects_a_different_genesis_hash_before_asking_for_headers()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.features", _ => new { genesis_hash = ChainProfiles.Testnet.GenesisHash });
        await using var client = await ConnectAsync(server);

        await Assert.ThrowsAsync<WrongChainException>(
            () => client.VerifyChainAsync(Profile(), default).WaitAsync(Timeout));
        Assert.Equal(0, server.CallCount("blockchain.headers.subscribe"));
    }

    [Fact]
    public async Task Genesis_comparison_is_case_insensitive()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.features", _ => new { genesis_hash = ChainProfiles.Mainnet.GenesisHash.ToUpperInvariant() });
        Tip(server, 10);
        await using var client = await ConnectAsync(server);

        await client.VerifyChainAsync(Profile(), default).WaitAsync(Timeout);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("no-genesis")]
    [InlineData("not-an-object")]
    public async Task Rejects_a_server_that_does_not_report_a_genesis_hash(string mode)
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.features", _ => mode switch
        {
            "error" => throw new FakeElectrumError(-32601, "unknown method"),
            "no-genesis" => new { hash_function = "sha256" },
            _ => "nope",
        });
        await using var client = await ConnectAsync(server);

        await Assert.ThrowsAsync<WrongChainException>(
            () => client.VerifyChainAsync(Profile(), default).WaitAsync(Timeout));
    }

    [Fact]
    public async Task A_tip_below_the_first_checkpoint_is_accepted_on_the_genesis_alone()
    {
        await using var server = new FakeElectrumServer();
        Tip(server, 50);
        await using var client = await ConnectAsync(server);

        await client.VerifyChainAsync(Profile(CheckpointFor(100, 7)), default).WaitAsync(Timeout);
        Assert.Equal(0, server.CallCount("blockchain.block.header"));
    }

    [Fact]
    public async Task A_profile_without_checkpoints_relies_on_the_genesis_alone()
    {
        await using var server = new FakeElectrumServer();
        Tip(server, 5_000_000);
        await using var client = await ConnectAsync(server);

        await client.VerifyChainAsync(Profile(), default).WaitAsync(Timeout);
        Assert.Equal(0, server.CallCount("blockchain.block.header"));
    }

    [Theory]
    [InlineData("server-error")]
    [InlineData("short-header")]
    [InlineData("not-hex")]
    public async Task A_missing_or_malformed_checkpoint_header_is_a_wrong_chain(string mode)
    {
        await using var server = new FakeElectrumServer();
        Tip(server, 150);
        server.Handle("blockchain.block.header", _ => mode switch
        {
            "server-error" => throw new FakeElectrumError(-1, "no such block"),
            "short-header" => "00ff",
            _ => "zz",
        });
        await using var client = await ConnectAsync(server);

        await Assert.ThrowsAsync<WrongChainException>(
            () => client.VerifyChainAsync(Profile(CheckpointFor(100, 7)), default).WaitAsync(Timeout));
    }

    [Fact]
    public void The_shipped_mainnet_profile_has_checkpoints_the_check_can_use()
    {
        Assert.NotEmpty(ChainProfiles.Mainnet.Checkpoints);
        Assert.All(ChainProfiles.Mainnet.Checkpoints, c => Assert.Equal(64, c.BlockHash.Length));
    }
}
