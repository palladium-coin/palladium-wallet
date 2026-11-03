# 0007. Verify a Merkle proof for every confirmed transaction

- **Status:** Accepted
- **Area:** SPV
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

The indexing server is semi-trusted: it can lie about history. Without proof of inclusion it could fabricate a confirmed payment.

## Decision

Every confirmed transaction must come with a Merkle branch that `MerkleProof.Verify` checks against the merkle root of an anchored header (0006). The txid, position and branch are taken from the server, the root only from a verified header. A mismatch fails the sync with `SpvVerificationException`; unconfirmed transactions are shown as such and are never spendable.

## Consequences

- A malicious server cannot fabricate a confirmed transaction with a valid proof.
- Cost: one proof request per confirmed transaction, mitigated by caching verified state (0013) and progressive verification (0008).
- Script execution and double-spend detection are not covered; see `SECURITY.md`.

## References

- `src/Core/Spv/MerkleProof.cs`
- `src/Core/Spv/WalletSynchronizer.cs`
- `SECURITY.md`
