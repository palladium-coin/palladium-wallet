# 0009. Hand-written Electrum JSON-RPC client with pipelining and an in-flight cap

- **Status:** Accepted
- **Area:** Network
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

The wallet talks to an ElectrumX/Fulcrum-style indexing server (ports 50001/50002, not the node's P2P port 2333). No library matched the needs: protocol 1.4, a testable in-process transport and control over throughput on large wallets.

## Decision

Implement `ElectrumClient` directly over `TcpClient`/`SslStream`, newline-delimited JSON-RPC 2.0. A single-reader channel batches writes (like Electrum's buffered writer); a read loop completes pending requests by id; a semaphore caps in-flight requests (`DefaultMaxInFlight = 32`, configurable per connection) to avoid server throttling and disconnects. Typed helpers live in `ElectrumApi`. The class is deliberately not an interface: tests run against a real in-process server instead (0025).

## Consequences

- Full control over request pipelining and error mapping (`ElectrumServerException`).
- No single in-flight value suits every server, hence the per-connection knob.
- Protocol quirks (e.g. `server.version` allowed once per connection) are our responsibility; the server probe (0027) exists to catch server differences.

## References

- `src/Core/Net/ElectrumClient.cs`
- `src/Core/Net/ElectrumApi.cs`
