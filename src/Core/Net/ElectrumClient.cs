using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace PalladiumWallet.Core.Net;

/// <summary>
/// Client for the indexing server protocol (blueprint §10):
/// newline-delimited JSON-RPC 2.0 over TCP, optionally TLS with TOFU pinning.
/// Notifications (subscriptions) arrive on the <see cref="NotificationReceived"/> event.
/// </summary>
public sealed class ElectrumClient : IAsyncDisposable
{
    public const string ClientName = "PalladiumWallet";
    public const string ProtocolVersion = "1.4";

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private readonly Task _writeLoop;

    // Single-reader channel: tasks write the payloads without a lock;
    // the write loop drains everything in a single WriteAsync+FlushAsync —
    // identical to Electrum's asyncio buffered writer.
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    // Cap on in-flight requests (not in the write queue, but awaiting a
    // response from the server). The write loop already batches the send into a
    // single segment; this gate avoids flooding the server with thousands of
    // simultaneous requests on large wallets → no bursts of -101/-102 nor
    // connection drops. Writes still stay pipelined up to this degree.
    // Configurable per connection (see ConnectAsync): the right value trades off
    // initial-sync throughput against how aggressively a given server tolerates
    // concurrent requests before throttling — no single constant is right for
    // every server, so callers may raise/lower it instead of recompiling.
    public const int DefaultMaxInFlight = 32;

    // Every request is bounded: a server that stops answering without closing the socket
    // (half-open TCP, Android doze, a stalled or hostile server) would otherwise leave the
    // caller waiting forever. The timer starts once the request holds an in-flight slot, so
    // queueing behind the cap does not count. On expiry the request fails with
    // TimeoutException and the whole connection is marked dead (pending requests fail,
    // Disconnected is raised): after a missed answer the socket cannot be trusted, and a
    // late reply is dropped because the request is no longer pending.
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _inFlight;

    private long _nextId;

    public string Host { get; }
    public int Port { get; }
    public bool UseSsl { get; }
    public bool IsConnected => _tcp.Connected && !_cts.IsCancellationRequested;

    public event Action<string, JsonElement>? NotificationReceived;
    public event Action<Exception?>? Disconnected;

    private ElectrumClient(TcpClient tcp, Stream stream, string host, int port, bool useSsl,
        int maxInFlight, TimeSpan requestTimeout)
    {
        _requestTimeout = requestTimeout;
        _tcp = tcp;
        _stream = stream;
        Host = host;
        Port = port;
        UseSsl = useSsl;
        _inFlight = new SemaphoreSlim(maxInFlight, maxInFlight);
        _readLoop  = Task.Run(ReadLoopAsync);
        _writeLoop = Task.Run(WriteLoopAsync);
    }

    public static async Task<ElectrumClient> ConnectAsync(string host, int port, bool useSsl,
        CertificatePinStore? pins = null, CancellationToken ct = default,
        int maxInFlight = DefaultMaxInFlight, TimeSpan? requestTimeout = null,
        TimeSpan? handshakeTimeout = null)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct);
            Stream stream = tcp.GetStream();
            if (useSsl)
            {
                var pinOk = true;
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
                    (_, cert, _, _) =>
                    {
                        if (cert is null) return false;
                        pinOk = pins is null || pins.VerifyOrPin(host, port, cert);
                        return pinOk;
                    });
                try
                {
                    await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions { TargetHost = host }, ct);
                }
                catch (System.Security.Authentication.AuthenticationException) when (!pinOk)
                {
                    throw new CertificatePinMismatchException(host, port);
                }
                stream = ssl;
            }

            var client = new ElectrumClient(tcp, stream, host, port, useSsl, maxInFlight,
                requestTimeout ?? DefaultRequestTimeout);
            await client.RequestCoreAsync(handshakeTimeout ?? DefaultHandshakeTimeout,
                "server.version", ct, [ClientName, ProtocolVersion]);
            return client;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public Task<JsonElement> RequestAsync(string method, CancellationToken ct = default,
        params object?[] parameters) =>
        RequestCoreAsync(_requestTimeout, method, ct, parameters);

    private async Task<JsonElement> RequestCoreAsync(TimeSpan timeout, string method,
        CancellationToken ct, object?[] parameters)
    {
        // Gate before going in-flight: beyond MaxInFlight pending requests
        // we wait for a response to free a slot, instead of flooding the server.
        await _inFlight.WaitAsync(ct);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            // The read loop cancels _cts before failing the pending requests, so a request that
            // registers after that point lands here instead of waiting for a response that can
            // never arrive (the loop is gone).
            if (_cts.IsCancellationRequested)
            {
                _pending.TryRemove(id, out _);
                throw new IOException("Connection to the server closed.");
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            });

            _outgoing.Writer.TryWrite(payload);

            await using var registration = ct.Register(() =>
            {
                _pending.TryRemove(id, out _);
                tcs.TrySetCanceled(ct);
            });

            using var timeoutCts = new CancellationTokenSource(timeout);
            await using var timeoutRegistration = timeoutCts.Token.Register(() =>
            {
                if (!_pending.TryRemove(id, out _)) return;
                tcs.TrySetException(new TimeoutException(
                    $"No response to '{method}' from {Host}:{Port} within {timeout.TotalSeconds:0.#}s."));
                try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            });
            return await tcs.Task;
        }
        finally
        {
            _inFlight.Release();
        }
    }

    /// <summary>
    /// Drain loop: empties the channel into a single buffer → one WriteAsync+FlushAsync
    /// for all queued messages. When N requests are queued, they are
    /// transmitted in a single TCP segment instead of N serial flushes.
    /// </summary>
    private async Task WriteLoopAsync()
    {
        try
        {
            while (await _outgoing.Reader.WaitToReadAsync(_cts.Token))
            {
                using var ms = new MemoryStream(512);
                while (_outgoing.Reader.TryRead(out var data))
                {
                    ms.Write(data);
                    ms.WriteByte((byte)'\n');
                }
                await _stream.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), _cts.Token);
                await _stream.FlushAsync(_cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            foreach (var (_, pending) in _pending)
                pending.TrySetException(ex);
        }
    }

    /// <summary>
    /// Read loop with PipeReader: pooled buffers, zero per-response allocations
    /// (no intermediate string), JSON parsing directly from a byte span.
    /// </summary>
    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        var pipe = PipeReader.Create(_stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            while (true)
            {
                ReadResult result;
                try { result = await pipe.ReadAsync(_cts.Token); }
                catch (OperationCanceledException) { break; }

                var buffer = result.Buffer;
                try
                {
                    while (TrySliceLine(ref buffer, out var line))
                        if (!line.IsEmpty)
                            DispatchLine(line);
                }
                finally
                {
                    // consumed = everything we have consumed (up to the last \n)
                    // examined = everything we have looked at (up to the end of the buffer)
                    pipe.AdvanceTo(buffer.Start, buffer.End);
                }

                if (result.IsCompleted) break;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            await pipe.CompleteAsync();
            // Mark the connection dead first (IsConnected, RequestAsync, write loop) so nothing
            // can register a request after the pending ones are failed below.
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var (_, tcs) in _pending)
                tcs.TrySetException(failure ?? new IOException("Connection to the server closed."));
            _pending.Clear();
            Disconnected?.Invoke(failure);
        }
    }

    private static bool TrySliceLine(ref ReadOnlySequence<byte> buffer,
        out ReadOnlySequence<byte> line)
    {
        var pos = buffer.PositionOf((byte)'\n');
        if (pos is null) { line = default; return false; }
        line   = buffer.Slice(0, pos.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, pos.Value));
        return true;
    }

    private void DispatchLine(ReadOnlySequence<byte> line)
    {
        if (line.IsSingleSegment)
        {
            DispatchSpan(line.FirstSpan);
            return;
        }
        // Multi-segment (very long response): copy to ArrayPool then parse.
        var len = (int)line.Length;
        var buf = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            line.CopyTo(buf);
            DispatchSpan(buf.AsSpan(0, len));
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private void DispatchSpan(ReadOnlySpan<byte> utf8)
    {
        // JsonDocument.Parse via Utf8JsonReader: no intermediate string,
        // parsing directly from the pooled span.
        var reader = new Utf8JsonReader(utf8);
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            if (!_pending.TryRemove(idEl.GetInt64(), out var tcs)) return;
            if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                tcs.TrySetException(new ElectrumServerException(err.ToString()));
            else
                tcs.TrySetResult(root.GetProperty("result").Clone());
        }
        else if (root.TryGetProperty("method", out var methodEl))
        {
            NotificationReceived?.Invoke(
                methodEl.GetString()!,
                root.GetProperty("params").Clone());
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _outgoing.Writer.Complete();
        _tcp.Close();
        try { await _readLoop;  } catch { }
        try { await _writeLoop; } catch { }
        _cts.Dispose();
        _inFlight.Dispose();
    }
}

/// <summary>Error returned by the server ("error" field of the JSON-RPC response).</summary>
public sealed class ElectrumServerException(string error) : Exception(error);

/// <summary>
/// The server certificate changed with respect to the saved one (TOFU, §9).
/// It is unlocked with an explicit reset of the certificates.
/// </summary>
public sealed class CertificatePinMismatchException(string host, int port) : Exception(
    $"The TLS certificate of {host}:{port} has changed from the one saved. " +
    "If the server renewed its certificate, reset the SSL certificates.")
{
    public string Host { get; } = host;
    public int Port { get; } = port;
}
