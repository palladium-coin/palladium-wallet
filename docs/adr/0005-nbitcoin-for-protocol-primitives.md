# 0005. Use NBitcoin for protocol primitives, hand-write only the rest

- **Status:** Accepted
- **Area:** Crypto
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Reimplementing BIP32/39, address encoding, transaction serialization, signing or PSBT is a classic source of subtle, fund-losing bugs.

## Decision

Build on NBitcoin for the custom network definition, BIP32/39, addresses, transactions, PSBT, signing, encoding and hashing. Hand-written code is limited to what NBitcoin does not provide for this chain: the Electrum JSON-RPC client (0009), SPV sync with Merkle verification and checkpoint anchoring (0006, 0007), coin selection and fee policy (0017), SLIP-132 variants beyond xpub/xprv (0014) and the encrypted wallet container (0012).

## Consequences

- Smaller audited surface; cross-implementation golden vectors catch divergence.
- NBitcoin assumes Bitcoin retargeting, which is why PoW validation is skipped (0006).
- A dependency upgrade can change behaviour; golden-vector tests must pass before bumping.

## References

- `src/Core/Chain/PalladiumNetworks.cs`
- `src/Core/Crypto/Slip132.cs`
