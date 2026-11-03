using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Storage;

namespace PalladiumWallet.Tests.Net;

/// <summary>Concurrency guarantees: the in-flight cap, disposal under load, exclusive wallet lock.</summary>
public class ConcurrencyTests
{
    // Generous: the rest of the suite (property tests) saturates the CPU while these run.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task InFlightRequests_AreCappedAtTheConfiguredLimit()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("hold", _ => FakeElectrumServer.NoResponse); // received, never answered
        await using var client = await ElectrumClient.ConnectAsync(
            server.Host, server.Port, useSsl: false, maxInFlight: 4);

        using var cts = new CancellationTokenSource();
        var requests = Enumerable.Range(0, 20)
            .Select(_ => client.RequestAsync("hold", cts.Token)).ToList();

        await WaitFor(() => server.CallCount("hold") == 4);
        await Task.Delay(250); // the other 16 must stay queued client-side, not reach the server
        Assert.Equal(4, server.CallCount("hold"));

        cts.Cancel();
        foreach (var r in requests)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.WaitAsync(Timeout));
    }

    [Fact]
    public async Task CompletedRequests_ReleaseTheirSlot_SoAllRequestsEventuallyFinish()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("echo", p => p[0].GetInt32());
        await using var client = await ElectrumClient.ConnectAsync(
            server.Host, server.Port, useSsl: false, maxInFlight: 2);

        var results = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(i => client.RequestAsync("echo", default, i))).WaitAsync(Timeout);

        Assert.Equal(Enumerable.Range(0, 100), results.Select(r => r.GetInt32()));
    }

    [Fact]
    public async Task DisposingTheClient_WithRequestsInFlight_DoesNotHang()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("hold", _ => FakeElectrumServer.NoResponse);
        var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);
        var pending = Enumerable.Range(0, 10).Select(_ => client.RequestAsync("hold")).ToList();
        await WaitFor(() => server.CallCount("hold") == 10);

        await client.DisposeAsync().AsTask().WaitAsync(Timeout);

        foreach (var p in pending)
            await Assert.ThrowsAnyAsync<Exception>(() => p.WaitAsync(Timeout)); // failed, not stuck
    }

    [Fact]
    public async Task WalletLock_UnderContention_HasExactlyOneWinner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plm-lock-{Guid.NewGuid()}.wallet.json");
        try
        {
            using var start = new ManualResetEventSlim(false);
            var attempts = Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => { start.Wait(); return WalletLock.TryAcquire(path); }))
                .ToList();
            start.Set();
            var locks = await Task.WhenAll(attempts);

            try
            {
                Assert.Single(locks, l => l is not null);
                Assert.Equal(15, locks.Count(l => l is null));
            }
            finally { foreach (var l in locks) l?.Dispose(); }

            // Released → acquirable again (no stale lock left behind).
            using var again = WalletLock.TryAcquire(path);
            Assert.NotNull(again);
        }
        finally { try { File.Delete(path); File.Delete(path + ".lock"); } catch { } }
    }
}
