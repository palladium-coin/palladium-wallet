using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Net;

namespace PalladiumWallet.Tests.Net;

/// <summary>Servers announced by a peer are listed but never trusted or tried on their own.</summary>
public class ServerTrustTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"plm-trust-{Guid.NewGuid()}.json");

    [Theory]
    [InlineData("example.org", true)]
    [InlineData("electrum.example.org", true)]
    [InlineData("173.212.224.67", true)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("localhost", false)]
    [InlineData("LOCALHOST", false)]
    [InlineData("a.localhost", false)]
    [InlineData("printer.local", false)]
    [InlineData("db.internal", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.1.2.3", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("bad host", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPublicHost_accepts_only_hosts_a_public_server_can_have(string? host, bool expected) =>
        Assert.Equal(expected, ServerRegistry.IsPublicHost(host));

    [Fact]
    public void An_oversized_host_is_rejected() =>
        Assert.False(ServerRegistry.IsPublicHost(new string('a', 254)));

    [Fact]
    public async Task Discovery_skips_private_hosts_and_fixes_out_of_range_ports()
    {
        var path = TempPath();
        try
        {
            await using var server = new FakeElectrumServer();
            server.Handle("server.peers.subscribe", _ => new object[]
            {
                new object[] { "1.1.1.1", "ok.example.org", new[] { "v1.4", "t50001", "s50002" } },
                new object[] { "1.1.1.2", "127.0.0.1", new[] { "v1.4", "t50001" } },
                new object[] { "1.1.1.3", "192.168.0.5", new[] { "v1.4", "s50002" } },
                new object[] { "1.1.1.4", "localhost", new[] { "v1.4", "t50001" } },
                new object[] { "1.1.1.5", "badport.example.org", new[] { "v1.4", "t99999", "s70000" } },
            });
            await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);
            var registry = new ServerRegistry(ChainProfiles.Mainnet, path);

            var added = await registry.DiscoverAsync(client);

            Assert.Equal(2, added);
            Assert.DoesNotContain(registry.All, s => s.Host is "127.0.0.1" or "192.168.0.5" or "localhost");
            var fixedPorts = registry.All.Single(s => s.Host == "badport.example.org");
            Assert.Equal(ChainProfiles.Mainnet.DefaultTcpPort, fixedPorts.TcpPort);
            Assert.Equal(ChainProfiles.Mainnet.DefaultSslPort, fixedPorts.SslPort);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Trusted_and_Default_are_the_bootstrap_servers_only()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new KnownServer("stranger.example.org", 50001, 50002),
            }));
            var registry = new ServerRegistry(ChainProfiles.Mainnet, path);

            Assert.Contains(registry.All, s => s.Host == "stranger.example.org"); // listed, selectable
            Assert.DoesNotContain(registry.Trusted, s => s.Host == "stranger.example.org");
            Assert.Equal(ChainProfiles.Mainnet.BootstrapServers.Count, registry.Trusted.Count);
            Assert.Equal(ChainProfiles.Mainnet.BootstrapServers[0].Host, registry.Default!.Host);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_profile_without_bootstrap_servers_has_no_default_even_with_discovered_ones()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new KnownServer("stranger.example.org", 50001, 50002),
            }));
            var registry = new ServerRegistry(ChainProfiles.Regtest, path);

            Assert.Null(registry.Default);
            Assert.Empty(registry.Trusted);
        }
        finally { File.Delete(path); }
    }
}
