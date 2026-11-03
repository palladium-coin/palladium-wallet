# 0025. Test the network layer against an in-process fake server

- **Status:** Accepted
- **Area:** Testing
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

SPV code must be proven to reject lying servers, which cannot be done reliably against real servers, and mocks of `ElectrumClient` would hide transport bugs.

## Decision

`tests/PalladiumWallet.Tests/Net/FakeElectrumServer.cs` is a loopback TCP server with optional TLS, per-method handlers and call counters. Tests program it to return wrong Merkle proofs, drop connections or throttle, and assert that `ElectrumClient`, `WalletSynchronizer`, `TransactionInspector` and certificate pinning react correctly. The client is not made an interface on purpose.

## Consequences

- Real sockets, real serialization, deterministic adversarial scenarios.
- New protocol behaviour means extending the fake, not mocking.

## References

- `tests/PalladiumWallet.Tests/Net/FakeElectrumServer.cs`
- `tests/PalladiumWallet.Tests/Net/ElectrumClientTests.cs`
