# 0034. Bound every network request with a timeout

- **Status:** Accepted
- **Area:** Network
- **Recorded:** 2026-11-03

## Context

`ElectrumClient.RequestAsync` waited until the server answered or the caller cancelled. The only timeout in the stack was the 8 s ping of the ViewModel keep-alive, which does not run while a sync is in progress. A server that stops answering without closing the socket (half-open TCP, Android Doze, a stalled or hostile server) left a running sync waiting forever, with no error and no automatic recovery.

## Decision

- Every request has a timeout, `ElectrumClient.DefaultRequestTimeout` (30 s). The `server.version` handshake has its own, shorter `DefaultHandshakeTimeout` (10 s). Both are parameters of `ConnectAsync` (`requestTimeout`, `handshakeTimeout`), so tests use a few hundred milliseconds.
- The timer starts when the request obtains its in-flight slot: waiting behind the cap (ADR 0009) does not count.
- On expiry the request fails with `TimeoutException` (naming the method and server) and the **whole connection is marked dead**: the other pending requests fail, `IsConnected` becomes false, `Disconnected` is raised and later requests fail fast with `IOException`. After a missed answer the socket cannot be trusted, and the ViewModel reconnects through the existing path. A late reply is dropped because the request is no longer pending.
- The keep-alive is left as it is (it skips ticks while syncing): the request timeout now bounds a stalled sync, so a second mechanism is not needed.

## Consequences

- A stalled server makes a sync fail within the timeout instead of hanging; the existing retry and reconnect logic takes over.
- A legitimately slow reply above 30 s is treated as a dead connection. The largest realistic replies (header batches, large transactions, busy-address histories) are far below that on a working link; the value is a constructor parameter if a deployment needs more.
- One slow request tears down the shared connection and the requests in flight with it. This is deliberate: it is cheaper than a half-open socket that keeps accepting writes.

## References

- `src/Core/Net/ElectrumClient.cs`
- `tests/PalladiumWallet.Tests/Net/ElectrumClientTests.cs`
- Related: 0009
