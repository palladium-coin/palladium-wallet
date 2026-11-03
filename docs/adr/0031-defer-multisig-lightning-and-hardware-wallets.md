# 0031. Explicitly defer multisig, Lightning, Tor and hardware wallets

- **Status:** Accepted
- **Area:** Scope
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Each of these adds large attack surface or UX complexity. Shipping a half-working version in a wallet is worse than not shipping it.

## Decision

The first release excludes Lightning, M-of-N multisig, hardware-wallet integration, Tor/proxy support, multi-server pooling and RBF/CPFP fee-bumping UI. Where the profile already defines the constants (multisig SLIP-132 headers, BIP48/BIP86 purposes), the unimplemented paths throw rather than silently producing an incorrect wallet. Coin control exists in `TransactionFactory` (manual UTXO selection) and freezing in the cache; `SECURITY.md` lists the limitations users must know.

## Consequences

- A smaller, auditable v1.
- Network traffic reveals which addresses are queried (no Tor); this is stated in the threat model.
- Each deferred feature needs its own ADR when taken up.

## References

- `SECURITY.md`
- `src/Core/Crypto/DerivationPaths.cs`
