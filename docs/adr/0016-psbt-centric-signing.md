# 0016. Route every signing flow through PSBT

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Offline, air-gapped and hardware-assisted signing all need a transport format, and a wallet that signs through a different path for each case multiplies the code that handles keys.

## Decision

`TransactionFactory.Build` produces a `BuiltTransaction` that always carries a PSBT. Accounts that hold keys also return the signed transaction; watch-only accounts return the unsigned PSBT for export and offline signing.

## Consequences

- One build path, interoperable with other PSBT tools.
- Hardware-wallet and multisig integration (not in v1) can reuse the same format.
- Cross-implementation tests compare PSBTs to a reference wallet; a differing PSBT or txid is a blocking bug.

## References

- `src/Core/Wallet/TransactionFactory.cs`
