using System.Text.Json;
using PalladiumWallet.Core.Net;

namespace PalladiumWallet.Tests.Net;

/// <summary>
/// Tests for the JSON-RPC transport against an in-process fake ElectrumX server
/// (real loopback TCP socket: framing, pipelining, and failure paths are the
/// same code paths used in production).
/// </summary>
public class ElectrumClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Connessione_e_richiesta_fanno_roundtrip()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.banner", _ => "benvenuto");

        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        Assert.True(client.IsConnected);
        Assert.Equal("benvenuto", await client.BannerAsync());
        // ConnectAsync performs the protocol handshake up front.
        Assert.Equal(1, server.CallCount("server.version"));
    }

    [Fact]
    public async Task Un_errore_del_server_diventa_ElectrumServerException()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.transaction.get",
            _ => throw new FakeElectrumError(-32600, "tx non trovata"));

        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var ex = await Assert.ThrowsAsync<ElectrumServerException>(
            () => client.GetTransactionAsync("00"));
        Assert.Contains("tx non trovata", ex.Message);
        Assert.Contains("-32600", ex.Message);
    }

    [Fact]
    public async Task Richieste_parallele_ricevono_ciascuna_la_propria_risposta()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("echo", p => p[0].GetInt32());

        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        // More requests than the in-flight cap (32): exercises both the write
        // batching and the id → response correlation.
        var tasks = Enumerable.Range(0, 200)
            .Select(i => client.RequestAsync("echo", default, i))
            .ToList();
        var results = await Task.WhenAll(tasks);

        for (var i = 0; i < results.Length; i++)
            Assert.Equal(i, results[i].GetInt32());
    }

    [Fact]
    public async Task Le_notifiche_arrivano_sull_evento_NotificationReceived()
    {
        await using var server = new FakeElectrumServer();
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var received = new TaskCompletionSource<(string Method, JsonElement Params)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += (method, p) => received.TrySetResult((method, p));

        await server.NotifyAsync("blockchain.headers.subscribe", new object[]
        {
            new { height = 4242, hex = "00" },
        });

        var (method, parameters) = await received.Task.WaitAsync(Timeout);
        Assert.Equal("blockchain.headers.subscribe", method);
        Assert.Equal(4242, parameters[0].GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task La_chiusura_del_server_fa_fallire_le_richieste_pendenti_e_notifica_Disconnected()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.banner", _ => FakeElectrumServer.NoResponse);

        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += _ => disconnected.TrySetResult();

        var pending = client.BannerAsync();
        server.DropClients();

        await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(Timeout));
        await disconnected.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_malformed_line_from_the_server_fails_pending_requests_and_reports_the_error()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.banner", _ => FakeElectrumServer.NoResponse);

        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += ex => disconnected.TrySetResult(ex);

        var pending = client.BannerAsync();
        await server.SendRawAsync("this is not json");

        await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(Timeout));
        Assert.NotNull(await disconnected.Task.WaitAsync(Timeout)); // failure, not a clean close
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Requests_after_the_connection_died_fail_fast_instead_of_hanging()
    {
        await using var server = new FakeElectrumServer();
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += _ => disconnected.TrySetResult();
        await server.SendRawAsync("garbage"); // kills the read loop
        await disconnected.Task.WaitAsync(Timeout);

        // The read loop is gone: nobody would ever complete this request.
        await Assert.ThrowsAnyAsync<IOException>(() => client.RequestAsync("server.ping").WaitAsync(Timeout));
    }

    [Fact]
    public async Task Una_risposta_molto_grande_arriva_integra()
    {
        // A payload far beyond the PipeReader's segment size forces the
        // multi-segment dispatch path (ArrayPool copy) instead of the fast span.
        var big = new string('x', 256 * 1024);
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.transaction.get", _ => big);

        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var result = await client.GetTransactionAsync("00").WaitAsync(Timeout);
        Assert.Equal(big, result);
    }

    [Fact]
    public async Task A_request_without_an_answer_fails_with_TimeoutException()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.banner", _ => FakeElectrumServer.NoResponse);
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port,
            useSsl: false, requestTimeout: TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.BannerAsync().WaitAsync(Timeout));
        Assert.Contains("server.banner", ex.Message);
    }

    [Fact]
    public async Task After_a_timeout_the_client_is_disconnected_and_later_requests_fail_fast()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.banner", _ => FakeElectrumServer.NoResponse);
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port,
            useSsl: false, requestTimeout: TimeSpan.FromMilliseconds(300));
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += _ => disconnected.TrySetResult();

        await Assert.ThrowsAsync<TimeoutException>(() => client.BannerAsync().WaitAsync(Timeout));
        await disconnected.Task.WaitAsync(Timeout);

        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<IOException>(() => client.BannerAsync().WaitAsync(Timeout));
    }

    [Fact]
    public async Task A_timeout_fails_the_other_pending_requests_too()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.banner", _ => FakeElectrumServer.NoResponse);
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port,
            useSsl: false, requestTimeout: TimeSpan.FromMilliseconds(300));

        var a = client.BannerAsync();
        var b = client.BannerAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => a.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<Exception>(() => b.WaitAsync(Timeout));
    }

    [Fact]
    public async Task A_slow_answer_within_the_timeout_still_succeeds()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.banner", _ => { Thread.Sleep(200); return "hello"; });
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port,
            useSsl: false, requestTimeout: TimeSpan.FromSeconds(10));

        Assert.Equal("hello", await client.BannerAsync().WaitAsync(Timeout));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task A_handshake_that_never_completes_times_out()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.version", _ => FakeElectrumServer.NoResponse);

        await Assert.ThrowsAsync<TimeoutException>(() => ElectrumClient.ConnectAsync(
            server.Host, server.Port, useSsl: false,
            handshakeTimeout: TimeSpan.FromMilliseconds(300)).WaitAsync(Timeout));
    }

    [Fact]
    public async Task Waiting_for_an_in_flight_slot_does_not_count_towards_the_timeout()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.banner", _ => { Thread.Sleep(250); return "ok"; });
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port,
            useSsl: false, maxInFlight: 1, requestTimeout: TimeSpan.FromMilliseconds(600));

        // Three serial requests of ~250 ms each: the last one waits ~500 ms for the slot.
        var all = await Task.WhenAll(client.BannerAsync(), client.BannerAsync(), client.BannerAsync())
            .WaitAsync(Timeout);
        Assert.All(all, b => Assert.Equal("ok", b));
    }

    [Fact]
    public async Task La_cancellazione_del_token_annulla_la_richiesta_pendente()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("server.banner", _ => FakeElectrumServer.NoResponse);

        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        using var cts = new CancellationTokenSource();
        var pending = client.BannerAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
    }

    [Fact]
    public async Task I_wrapper_tipizzati_del_protocollo_parsano_le_risposte()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.scripthash.listunspent", _ => new object[]
        {
            new { tx_hash = "aa".PadLeft(64, '0'), tx_pos = 1, value = 12_345L, height = 99 },
        });
        server.Handle("blockchain.transaction.broadcast", p => p[0].GetString()!.Length.ToString());
        server.Handle("blockchain.estimatefee", _ => 0.00012m);
        server.Handle("blockchain.relayfee", _ => 0.00001m);
        server.Handle("server.ping", _ => null);

        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var unspent = Assert.Single(await client.ListUnspentAsync("sh"));
        Assert.Equal(12_345L, unspent.ValueSats);
        Assert.Equal(1, unspent.TxPos);
        Assert.Equal(99, unspent.Height);

        Assert.Equal("4", await client.BroadcastAsync("beef"));
        Assert.Equal(0.00012m, await client.EstimateFeeAsync(2));
        Assert.Equal(0.00001m, await client.RelayFeeAsync());
        await client.PingAsync(); // must not throw
    }

    [Fact]
    public async Task Ssl_con_tofu_accetta_il_primo_certificato_e_rifiuta_un_certificato_cambiato()
    {
        var pinsPath = Path.Combine(Path.GetTempPath(), $"plm-pins-{Guid.NewGuid()}.json");
        try
        {
            var pins = new CertificatePinStore(pinsPath);
            int port;

            // First contact: any certificate is pinned (trust on first use).
            using (var cert1 = FakeElectrumServer.CreateSelfSignedCertificate("primo"))
            {
                await using var server = new FakeElectrumServer(cert1);
                port = server.Port;
                await using var client = await ElectrumClient.ConnectAsync(
                    server.Host, port, useSsl: true, pins);
                Assert.True(client.IsConnected);
                Assert.True(client.UseSsl);
            }

            // Same host:port, different certificate: the pin must block the connection.
            using (var cert2 = FakeElectrumServer.CreateSelfSignedCertificate("secondo"))
            {
                await using var server = await RebindAsync(cert2, port);
                await Assert.ThrowsAsync<CertificatePinMismatchException>(() =>
                    ElectrumClient.ConnectAsync(server.Host, port, useSsl: true, pins));
            }
        }
        finally
        {
            File.Delete(pinsPath);
        }
    }

    /// <summary>Rebinds a fresh server to the port just released by the previous one.</summary>
    private static async Task<FakeElectrumServer> RebindAsync(
        System.Security.Cryptography.X509Certificates.X509Certificate2 cert, int port)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FakeElectrumServer(cert, port); }
            catch (System.Net.Sockets.SocketException) when (attempt < 20)
            {
                await Task.Delay(50);
            }
        }
    }
}
