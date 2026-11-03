using System.Reflection;

namespace PalladiumWallet.Tests.Net;

/// <summary>
/// Opt-in conformance run of the ServerProbe against real Electrum/Fulcrum servers.
/// Does nothing (passes) unless <c>PALLADIUM_TEST_SERVERS</c> is set, so the default
/// <c>dotnet test</c> stays offline and deterministic.
///
///   PALLADIUM_TEST_SERVERS=host1:50002,host2:50002   servers to probe (comma separated)
///   PALLADIUM_TEST_SSL=1                              use TLS
///   PALLADIUM_TEST_NET=mainnet|testnet|regtest        network (default mainnet)
///
/// Example: PALLADIUM_TEST_SERVERS=1.2.3.4:50002 PALLADIUM_TEST_SSL=1 dotnet test --filter LiveServer
/// </summary>
[Collection("AppPaths")] // serialized with other tests touching process-wide state (Console, AppPaths)
public class LiveServerTests
{
    [Fact]
    public async Task LiveServer_PassesProbe()
    {
        var servers = Environment.GetEnvironmentVariable("PALLADIUM_TEST_SERVERS");
        if (string.IsNullOrWhiteSpace(servers)) return;

        var args = new List<string>();
        foreach (var s in servers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            args.AddRange(["--server", s]);
        if (Environment.GetEnvironmentVariable("PALLADIUM_TEST_SSL") is "1" or "true") args.Add("--ssl");
        if (Environment.GetEnvironmentVariable("PALLADIUM_TEST_NET") is { Length: > 0 } net) args.AddRange(["--net", net]);

        // The probe is a top-level-statements exe: call its entry point rather than shelling out.
        var entry = Assembly.Load("PalladiumWallet.ServerProbe").EntryPoint
            ?? throw new InvalidOperationException("ServerProbe has no entry point.");

        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int exit;
        // The synthesized entry point is a synchronous Main(string[]) → int wrapper.
        try { exit = await Task.Run(() => (int)entry.Invoke(null, [args.ToArray()])!); }
        finally { Console.SetOut(original); }

        Assert.True(exit == 0, captured.ToString());
    }
}
