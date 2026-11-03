# 0035. Cap the length of a server line

- **Status:** Accepted
- **Area:** Network
- **Recorded:** 2026-11-03

## Context

`ElectrumClient.ReadLoopAsync` accumulated bytes until it found a newline, with no maximum. A malicious, misconfigured or compromised server could send an endless line and drive memory use up until the process died, which is cheap to do and fatal on Android. `SECURITY.md` treats denial of service against the indexing server as out of scope, but here it is the client that dies, and the fix is small.

## Decision

- A single JSON-RPC line is capped at `ElectrumClient.DefaultMaxLineBytes` (16 MiB), a parameter of `ConnectAsync` (`maxLineBytes`). The default is well above the largest legitimate replies: header batches (a few hundred KB), a large transaction (up to about 2 MB of hex) and the history of a very active address (a few MB).
- The check runs as the buffer grows, **before** a newline arrives, and also on complete lines, so neither an unterminated stream nor one huge terminated line is buffered past the cap (apart from what a single read delivers).
- On excess the read loop fails with `InvalidDataException`. This reuses the protocol-error path: pending requests fail with that exception, the connection is marked dead, `Disconnected(ex)` is raised and later requests fail fast.

## Consequences

- One hostile server can no longer exhaust the memory of the client with an unbounded line.
- A legitimate reply larger than 16 MiB would be refused; none is expected on this chain, and the cap is a parameter.
- No new parser is introduced, so no new fuzz target.

## References

- `src/Core/Net/ElectrumClient.cs`
- `tests/PalladiumWallet.Tests/Net/ElectrumClientTests.cs`
- Related: 0009, 0034
