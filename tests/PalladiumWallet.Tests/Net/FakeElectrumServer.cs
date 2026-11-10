using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace PalladiumWallet.Tests.Net;

/// <summary>
/// Error a handler can throw to make the fake server reply with a JSON-RPC
/// error object (e.g. code -102 "server busy" to exercise the retry path).
/// </summary>
public sealed class FakeElectrumError(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// In-process ElectrumX-like server for tests: newline-delimited JSON-RPC 2.0
/// over a loopback TCP socket, optionally TLS with a self-signed certificate.
/// Handlers are registered per method and receive the request "params" array;
/// whatever they return is serialised as the "result". "server.version" is
/// pre-registered. Calls are counted per method (cache/retry assertions).
/// </summary>
public sealed class FakeElectrumServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly X509Certificate2? _certificate;
    private readonly ConcurrentDictionary<string, Func<JsonElement, object?>> _handlers = new();
    private readonly ConcurrentDictionary<string, int> _callCounts = new();
    private readonly ConcurrentBag<(Stream Stream, SemaphoreSlim WriteLock, TcpClient Tcp)> _clients = [];

    /// <summary>Sentinel: a handler returning this leaves the request unanswered (pending forever).</summary>
    public static readonly object NoResponse = new();

    public FakeElectrumServer(X509Certificate2? certificate = null, int port = 0)
    {
        _certificate = certificate;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Handle("server.version", _ => new[] { "FakeElectrumX 1.0", "1.4" });
        // Reports the mainnet genesis, as a real server does; override to simulate another chain.
        Handle("server.features", _ => new
        {
            genesis_hash = PalladiumWallet.Core.Chain.ChainProfiles.Mainnet.GenesisHash,
            hash_function = "sha256",
        });
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string Host => "127.0.0.1";

    /// <summary>Registers (or replaces) the handler for a JSON-RPC method.</summary>
    public void Handle(string method, Func<JsonElement, object?> handler) =>
        _handlers[method] = handler;

    private int _accepted, _connected;

    /// <summary>Connections accepted since the server started (TLS handshake completed, if any).</summary>
    public int AcceptedCount => Volatile.Read(ref _accepted);

    /// <summary>Connections currently open: a client that disposed its connection no longer counts.</summary>
    public int ConnectedCount => Volatile.Read(ref _connected);

    public int CallCount(string method) => _callCounts.GetValueOrDefault(method);

    public void ResetCallCounts() => _callCounts.Clear();

    /// <summary>Pushes a JSON-RPC notification to every connected client.</summary>
    public async Task NotifyAsync(string method, object parameters)
    {
        var line = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
        });
        foreach (var (stream, writeLock, _) in _clients)
            await WriteLineAsync(stream, writeLock, line);
    }

    /// <summary>Sends an arbitrary (possibly malformed) line to every connected client.</summary>
    public async Task SendRawAsync(string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        foreach (var (stream, writeLock, _) in _clients)
            await WriteLineAsync(stream, writeLock, bytes);
    }

    /// <summary>Sends raw bytes with no trailing newline (an unterminated, still-growing line).</summary>
    public async Task SendUnterminatedAsync(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        foreach (var (stream, writeLock, _) in _clients)
        {
            await writeLock.WaitAsync();
            try { await stream.WriteAsync(bytes); await stream.FlushAsync(); }
            catch (Exception) { }
            finally { writeLock.Release(); }
        }
    }

    /// <summary>Abruptly closes every accepted connection (tests the client's failure paths).</summary>
    public void DropClients()
    {
        foreach (var (_, _, tcp) in _clients)
            tcp.Close();
    }

    /// <summary>Self-signed certificate for TLS tests (exportable, valid now).</summary>
    public static X509Certificate2 CreateSelfSignedCertificate(string cn = "fake-electrum")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // PFX roundtrip so the private key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => ServeClientAsync(tcp));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    private async Task ServeClientAsync(TcpClient tcp)
    {
        var counted = false;
        try
        {
            Stream stream = tcp.GetStream();
            if (_certificate is not null)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(_certificate);
                stream = ssl;
            }

            var writeLock = new SemaphoreSlim(1, 1);
            _clients.Add((stream, writeLock, tcp));
            Interlocked.Increment(ref _accepted);
            Interlocked.Increment(ref _connected);
            counted = true;

            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                if (line.Length == 0) continue;
                await HandleRequestAsync(stream, writeLock, line);
            }
        }
        catch (Exception)
        {
            // Client went away or TLS failed (e.g. rejected pin): normal in tests.
        }
        finally
        {
            if (counted)
                Interlocked.Decrement(ref _connected);
        }
    }

    private async Task HandleRequestAsync(Stream stream, SemaphoreSlim writeLock, string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetInt64();
        var method = root.GetProperty("method").GetString()!;
        var parameters = root.TryGetProperty("params", out var p) ? p : default;

        _callCounts.AddOrUpdate(method, 1, (_, n) => n + 1);

        byte[] payload;
        if (!_handlers.TryGetValue(method, out var handler))
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code = -32601, message = $"unknown method '{method}'" },
            });
        }
        else
        {
            try
            {
                var result = handler(parameters);
                if (ReferenceEquals(result, NoResponse))
                    return;
                payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    jsonrpc = "2.0",
                    id,
                    result,
                });
            }
            catch (FakeElectrumError err)
            {
                payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = err.Code, message = err.Message },
                });
            }
        }

        await WriteLineAsync(stream, writeLock, payload);
    }

    private static async Task WriteLineAsync(Stream stream, SemaphoreSlim writeLock, byte[] payload)
    {
        await writeLock.WaitAsync();
        try
        {
            await stream.WriteAsync(payload);
            stream.WriteByte((byte)'\n');
            await stream.FlushAsync();
        }
        catch (Exception)
        {
            // Connection already gone: irrelevant for the test in progress.
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        DropClients();
        try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}
