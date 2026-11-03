# 0027. Server probe tool and opt-in live-server test

- **Status:** Accepted
- **Area:** Testing
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Servers differ (Fulcrum, ElectrumX, electrs), can be on the wrong chain, lag behind or serve invalid proofs. The fake server cannot reveal how a real deployment behaves.

## Decision

`tests/PalladiumWallet.ServerProbe` is a read-only CLI that runs black-box checks against real servers through the wallet's own `ElectrumClient`: handshake and TLS fingerprint, `server.features` (genesis and `sha256`), tip freshness, genesis header, header linkage, a Merkle proof of a recent coinbase against the header root, fees, peers, error handling, latency and a pipelined burst, plus a cross-server tip comparison. `LiveServerTests` runs it from `dotnet test` only when `PALLADIUM_TEST_SERVERS` is set, so the default run stays offline.

## Consequences

- Operators and developers can qualify a server before using it.
- The live test depends on third-party availability and is therefore opt-in.
- The probe reuses production client code, so a pass means the wallet can use the server.

## References

- `tests/PalladiumWallet.ServerProbe/Program.cs`
- `tests/PalladiumWallet.Tests/Net/LiveServerTests.cs`
