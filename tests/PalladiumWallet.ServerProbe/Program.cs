// Black-box conformance/health probe for Electrum-protocol servers (Fulcrum, ElectrumX, electrs).
// Reuses the wallet's own ElectrumClient, so a server that passes here is one the wallet can use.
// Read-only: never sends transactions and never touches wallet files.
using System.Diagnostics;
using System.Net.Security;
using System.Text.Json;
using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Spv;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h")) return Usage();

var profile = Opt("--net") switch
{
    "testnet" => ChainProfiles.Testnet,
    "regtest" => ChainProfiles.Regtest,
    _ => ChainProfiles.Mainnet,
};
var useSsl = args.Contains("--ssl");
var timeout = TimeSpan.FromSeconds(int.TryParse(Opt("--timeout"), out var t) ? t : 15);
var pings = int.TryParse(Opt("--pings"), out var p) ? p : 5;
var maxLag = int.TryParse(Opt("--max-lag"), out var l) ? l : 2;

var targets = new List<(string Host, int Port, bool Ssl)>();
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--server") targets.Add(ParseTarget(args[i + 1]));
if (Opt("--file") is { } file)
    foreach (var line in File.ReadAllLines(file))
        if (line.Trim() is { Length: > 0 } s && !s.StartsWith('#')) targets.Add(ParseTarget(s));
if (args.Contains("--registry"))
    foreach (var s in new ServerRegistry(profile, Path.GetTempFileName()).All)
        targets.Add((s.Host, s.PortFor(useSsl), useSsl));
if (targets.Count == 0) { Console.Error.WriteLine("No server given."); return Usage(); }

var failed = 0;
var heights = new List<(string Name, int Height)>();
foreach (var target in targets)
{
    Console.WriteLine($"\n=== {target.Host}:{target.Port}{(target.Ssl ? " (TLS)" : "")} [{profile.NetName}] ===");
    var (ok, height) = await Probe(target);
    if (!ok) failed++;
    if (height is { } h) heights.Add(($"{target.Host}:{target.Port}", h));
}

if (heights.Count > 1)
{
    var best = heights.Max(x => x.Height);
    Console.WriteLine($"\n=== Cross-server tip (best {best}) ===");
    foreach (var (name, h) in heights)
        Console.WriteLine($"  {(best - h > maxLag ? "WARN" : " ok ")} {name}: {h} (lag {best - h})");
}
Console.WriteLine($"\n{targets.Count - failed}/{targets.Count} server(s) passed.");
return failed == 0 ? 0 : 1;

// ---------------------------------------------------------------------------

async Task<(bool Ok, int? Height)> Probe((string Host, int Port, bool Ssl) s)
{
    var allOk = true;
    int? tip = null;

    async Task<T?> Step<T>(string name, Func<CancellationToken, Task<(T Value, string Detail, bool Warn)>> body)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            var (value, detail, warn) = await body(cts.Token);
            Console.WriteLine($"  {(warn ? "WARN" : " ok ")} {name,-22} {sw.ElapsedMilliseconds,5} ms  {detail}");
            return value;
        }
        catch (Exception ex)
        {
            allOk = false;
            var msg = ex is OperationCanceledException ? $"timeout after {timeout.TotalSeconds:0}s" : ex.Message;
            Console.WriteLine($"  FAIL {name,-22} {sw.ElapsedMilliseconds,5} ms  {msg}");
            return default;
        }
    }

    ElectrumClient? client = null;
    try
    {
        client = await Step("connect+handshake", async ct =>
        {
            var c = await ElectrumClient.ConnectAsync(s.Host, s.Port, s.Ssl, pins: null, ct);
            return (c, s.Ssl ? "TLS fingerprint " + await Fingerprint(s.Host, s.Port, ct) : "plaintext", !s.Ssl);
        });
        if (client is null) return (false, null);
        var c = client;
        await Step("server.banner", async ct =>
        {
            var r = await c.RequestAsync("server.banner", ct);
            var b = (r.GetString() ?? "").Replace('\n', ' ').Trim();
            return (0, b.Length > 60 ? b[..60] + "…" : b, false);
        });

        await Step("server.features", async ct =>
        {
            var r = await c.RequestAsync("server.features", ct);
            var genesis = r.TryGetProperty("genesis_hash", out var g) ? g.GetString() : null;
            var hashFn = r.TryGetProperty("hash_function", out var hf) ? hf.GetString() : null;
            if (genesis != profile.GenesisHash)
                throw new Exception($"genesis_hash {genesis} != expected {profile.GenesisHash} (wrong chain)");
            if (hashFn != "sha256") throw new Exception($"hash_function '{hashFn}' (expected sha256)");
            var pruning = r.TryGetProperty("pruning", out var pr) && pr.ValueKind == JsonValueKind.Number;
            return (0, $"genesis ok, pruning={(pruning ? pr.ToString() : "none")}", pruning);
        });

        var tipInfo = await Step("headers.subscribe", async ct =>
        {
            var r = await c.RequestAsync("blockchain.headers.subscribe", ct);
            var height = r.GetProperty("height").GetInt32();
            var header = BlockHeaderInfo.Parse(r.GetProperty("hex").GetString()!);
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(header.Timestamp);
            var stale = age.TotalSeconds > profile.BlockTimeSeconds * 30;
            return ((height, header), $"height {height}, tip age {FormatAge(age)}", stale);
        });
        if (tipInfo.header is null) return (false, null);
        tip = tipInfo.height;
        var (tipHeight, tipHeader) = tipInfo;

        await Step("genesis header", async ct =>
        {
            var hex = (await c.RequestAsync("blockchain.block.header", ct, 0)).GetString()!;
            var h = BlockHeaderInfo.Parse(hex);
            if (h.Hash.ToString() != profile.GenesisHash) throw new Exception($"header 0 hash {h.Hash}");
            return (0, "hash matches profile", false);
        });

        await Step("header chain linkage", async ct =>
        {
            var count = Math.Min(20, tipHeight);
            var start = tipHeight - count + 1;
            var r = await c.RequestAsync("blockchain.block.headers", ct, start, count);
            var raw = Convert.FromHexString(r.GetProperty("hex").GetString()!);
            var n = raw.Length / BlockHeaderInfo.Size;
            if (n == 0) throw new Exception("empty header batch");
            BlockHeaderInfo? prev = null;
            for (var i = 0; i < n; i++)
            {
                var h = BlockHeaderInfo.Parse(raw[(i * 80)..((i + 1) * 80)]);
                if (prev is not null && h.PrevHash != prev.Hash) throw new Exception($"broken link at height {start + i}");
                prev = h;
            }
            // The batch may be a block or two ahead of the earlier subscribe; only compare when equal.
            var note = start + n - 1 == tipHeight && prev!.Hash != tipHeader.Hash
                ? throw new Exception("batch tip differs from headers.subscribe tip")
                : "";
            return (0, $"{n} headers linked {note}".Trim(), false);
        });

        await Step("merkle proof (SPV)", async ct =>
        {
            // A block a few confirmations deep: its coinbase must verify against the header root.
            var height = Math.Max(1, tipHeight - 6);
            var hdr = BlockHeaderInfo.Parse((await c.RequestAsync("blockchain.block.header", ct, height)).GetString()!);
            var r = await c.RequestAsync("blockchain.transaction.id_from_pos", ct, height, 0, true);
            if (r.ValueKind != JsonValueKind.Object || !(r.TryGetProperty("tx_hash", out _) || r.TryGetProperty("tx_id", out _)) || !r.TryGetProperty("merkle", out _)) throw new Exception("unexpected id_from_pos reply: " + r.ToString()[..Math.Min(120, r.ToString().Length)]);
            var txid = uint256.Parse((r.TryGetProperty("tx_hash", out var th) ? th : r.GetProperty("tx_id")).GetString()!);
            var branch = r.GetProperty("merkle").EnumerateArray().Select(e => uint256.Parse(e.GetString()!)).ToList();
            if (!MerkleProof.Verify(txid, 0, branch, hdr.MerkleRoot))
                throw new Exception($"merkle proof for coinbase at height {height} does NOT match header root");
            return (0, $"coinbase {txid.ToString()[..12]}… @ {height} verified", false);
        });

        await Step("estimatefee/relayfee", async ct =>
        {
            var est = (await c.RequestAsync("blockchain.estimatefee", ct, 2)).GetDouble();
            var relay = (await c.RequestAsync("blockchain.relayfee", ct)).GetDouble();
            return (0, $"est(2 blk)={(est < 0 ? "n/a" : est.ToString("0.########"))} PLM/kB, relay={relay:0.########}", relay <= 0);
        });

        await Step("fee histogram", async ct =>
        {
            var r = await c.RequestAsync("mempool.get_fee_histogram", ct);
            return (0, $"{r.GetArrayLength()} buckets", false);
        });

        await Step("peers.subscribe", async ct =>
        {
            var r = await c.RequestAsync("server.peers.subscribe", ct);
            return (0, $"{r.GetArrayLength()} peers", false);
        });

        await Step("unknown scripthash", async ct =>
        {
            // Well-formed but unused scripthash must return an empty, not an error.
            var sh = new string('0', 64);
            var r = await c.RequestAsync("blockchain.scripthash.get_balance", ct, sh);
            var conf = r.GetProperty("confirmed").GetInt64();
            if (conf != 0) throw new Exception($"confirmed={conf} for an all-zero scripthash");
            return (0, "balance 0/0", false);
        });

        await Step("error handling", async ct =>
        {
            try { await c.RequestAsync("blockchain.block.header", ct, int.MaxValue); }
            catch (ElectrumServerException) { return (0, "bad height → JSON-RPC error", false); }
            throw new Exception("server answered a nonexistent height instead of an error");
        });

        await Step($"latency x{pings}", async ct =>
        {
            var times = new List<double>();
            for (var i = 0; i < pings; i++)
            {
                var sw = Stopwatch.StartNew();
                await c.RequestAsync("server.ping", ct);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            return (0, $"min {times.Min():0} / avg {times.Average():0} / max {times.Max():0} ms", times.Average() > 1500);
        });

        await Step("concurrent burst x50", async ct =>
        {
            var sw = Stopwatch.StartNew();
            var rs = await Task.WhenAll(Enumerable.Range(0, 50)
                .Select(i => c.RequestAsync("blockchain.block.header", ct, Math.Max(0, tipHeight - i))));
            if (rs.Any(r => r.ValueKind != JsonValueKind.String)) throw new Exception("malformed reply in burst");
            return (0, $"50 pipelined requests in {sw.ElapsedMilliseconds} ms", false);
        });

        await Step("tip still advancing", async ct =>
        {
            var r = await c.RequestAsync("blockchain.headers.subscribe", ct);
            var now = r.GetProperty("height").GetInt32();
            tip = now;
            return (0, $"height {now} (was {tipHeight})", now < tipHeight);
        });
    }
    finally
    {
        if (client is not null) await client.DisposeAsync();
    }
    Console.WriteLine(allOk ? "  => PASS" : "  => FAIL");
    return (allOk, tip);
}

static async Task<string> Fingerprint(string host, int port, CancellationToken ct)
{
    using var tcp = new System.Net.Sockets.TcpClient();
    await tcp.ConnectAsync(host, port, ct);
    string fp = "?";
    await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
    {
        if (cert is not null) fp = CertificatePinStore.Fingerprint(cert);
        return true;
    });
    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);
    return fp.Length > 23 ? fp[..23] + "…" : fp;
}

static string FormatAge(TimeSpan a) =>
    a.TotalMinutes < 120 ? $"{a.TotalMinutes:0} min" : a.TotalHours < 48 ? $"{a.TotalHours:0.#} h" : $"{a.TotalDays:0.#} d";

(string Host, int Port, bool Ssl) ParseTarget(string spec)
{
    var parts = spec.Trim().Split(':');
    var port = parts.Length > 1 ? int.Parse(parts[1]) : useSsl ? profile.DefaultSslPort : profile.DefaultTcpPort;
    return (parts[0], port, useSsl);
}

string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int Usage()
{
    Console.WriteLine("""
        PalladiumWallet server probe — conformance/health checks for Electrum-protocol servers

        usage: dotnet run --project tests/PalladiumWallet.ServerProbe -- [options]

          --server host[:port]   server to test (repeatable)
          --file PATH            text file, one host[:port] per line ('#' comments)
          --registry             test every server known for the network
          --ssl                  use TLS (default port 50002; plain 50001)
          --net mainnet|testnet|regtest   (default mainnet)
          --timeout SEC          per-check timeout (default 15)
          --pings N              latency samples (default 5)
          --max-lag N            blocks behind the best server before WARN (default 2)

        Checks: handshake/TLS, version, banner, features (genesis + sha256), tip freshness,
        genesis header, header linkage, Merkle proof vs header root, fees, peers, error
        handling, latency, pipelined burst. Exit code 1 if any server has a FAIL.
        """);
    return 2;
}
