using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PalladiumWallet.Tests.Net;

/// <summary>
/// A TCP proxy in front of a real Electrum server that lies about broadcasts, to check the broadcast
/// reconciliation (ADR 0047) against a real server: the client talks to the proxy, everything is forwarded
/// untouched except <c>blockchain.transaction.broadcast</c>.
/// </summary>
public sealed class LyingProxy : IAsyncDisposable
{
    public enum Mode
    {
        /// <summary>Forward everything, tell the truth.</summary>
        Honest,
        /// <summary>The server gets the transaction, but the client is told another txid.</summary>
        Swap,
        /// <summary>The server never gets the transaction; the client is told another txid.</summary>
        Drop,
    }

    public static readonly string WrongTxid = new('b', 64);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly Task _acceptLoop;

    public LyingProxy(string upstreamHost, int upstreamPort)
    {
        (_upstreamHost, _upstreamPort) = (upstreamHost, upstreamPort);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptAsync);
    }

    public string Host => "127.0.0.1";
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>What the next broadcasts do; can be changed while connections are open.</summary>
    public volatile Mode Behaviour = Mode.Honest;

    /// <summary>Number of broadcast requests that reached the proxy (forwarded or not).</summary>
    public int Broadcasts => Volatile.Read(ref _broadcasts);
    private int _broadcasts;

    private async Task AcceptAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => PumpAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    private async Task PumpAsync(TcpClient client)
    {
        using var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, _cts.Token);
            var broadcastIds = new System.Collections.Concurrent.ConcurrentDictionary<long, bool>();
            var clientStream = client.GetStream();
            var upstreamStream = upstream.GetStream();
            var clientLock = new SemaphoreSlim(1, 1);

            var up = Task.Run(async () =>      // client -> server
            {
                using var reader = new StreamReader(clientStream, Encoding.UTF8, false, 4096, leaveOpen: true);
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    if (IsBroadcast(line, out var id))
                    {
                        Interlocked.Increment(ref _broadcasts);
                        if (Behaviour == Mode.Drop)
                        {
                            await WriteAsync(clientStream, clientLock, Reply(id));   // never forwarded
                            continue;
                        }
                        broadcastIds[id] = true;
                    }
                    await upstreamStream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), _cts.Token);
                }
            });
            var down = Task.Run(async () =>    // server -> client
            {
                using var reader = new StreamReader(upstreamStream, Encoding.UTF8, false, 4096, leaveOpen: true);
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    if (Behaviour == Mode.Swap && TryId(line, out var id) && broadcastIds.TryRemove(id, out _))
                        line = Reply(id);
                    await WriteAsync(clientStream, clientLock, line);
                }
            });
            await Task.WhenAny(up, down);
        }
        catch (Exception) { /* a side went away: end of the connection */ }
        finally { client.Close(); upstream.Close(); }
    }

    private static string Reply(long id) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = WrongTxid });

    private static async Task WriteAsync(Stream stream, SemaphoreSlim gate, string line)
    {
        await gate.WaitAsync();
        try { await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")); }
        finally { gate.Release(); }
    }

    private static bool IsBroadcast(string line, out long id)
    {
        id = 0;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("method", out var m)
                && m.GetString() == "blockchain.transaction.broadcast"
                && doc.RootElement.TryGetProperty("id", out var i) && i.TryGetInt64(out id);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryId(string line, out long id)
    {
        id = 0;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("id", out var i) && i.TryGetInt64(out id);
        }
        catch (JsonException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}
