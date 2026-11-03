# 0040. Anchor headers up to a checkpoint above them

- **Status:** Accepted
- **Area:** SPV
- **Supersedes:** the anchoring direction in 0006 (the rest of 0006 stands)
- **Recorded:** 2026-11-03

## Context

ADR 0006 skips proof-of-work validation (LWMA cannot be recomputed by an SPV client) and relies on hardcoded checkpoints. `AnchorToCheckpointAsync` took the nearest checkpoint **at or below** a transaction's height and verified that the headers from the checkpoint **up to** that height were prev-hash linked. With PoW not checked, that chain proves nothing: a hostile server can fabricate a header for the block of a fake transaction (with any Merkle root) and chain fabricated headers after the real checkpoint hash; every link is valid and the Merkle proof then matches the fabricated root. The existing tests built their own synthetic chains, so they could not see it. A related gap (L-01): heights below the first checkpoint had no anchor at all.

## Decision

- Anchor **up to** the nearest checkpoint at or above the height. The headers from the transaction's height to the checkpoint height are downloaded, must be prev-hash linked, and the last one must hash to the hardcoded checkpoint. Forging that needs a SHA-256 preimage, not just a consistent set of fake headers.
- `_anchoredDownTo[checkpointHeight]` records the lowest height already proven to reach that checkpoint. A later, lower height walks only the new segment, whose top is the already-trusted header; a height inside the range is a no-op. Persisted as `SyncCache.AnchoredDownTo`.
- Heights below the first checkpoint are anchored to it (at most one checkpoint interval, 20,000 headers); this closes L-01 without new checkpoints.
- **Above the newest checkpoint** there is nothing to anchor to: only the Merkle proof against the server's header is checked. This is a known, documented limit; it is narrowed by adding a recent checkpoint at every release (taken from a trusted node).
- A wallet cache without `AnchoredDownTo` (written by an older version) is not trusted for `VerifiedAt`: every proof is verified again once. Cached headers and raw transactions are reused. A preloaded anchor is accepted only if its headers are also cached and the recorded height is not above its checkpoint.

## Consequences

- The "headers are anchored to a checkpoint" claim now holds against a fabricating server, for every height up to the newest checkpoint.
- Recent transactions (since the last release's checkpoint) stay protected by the Merkle proof alone. Releases should always ship a fresh checkpoint.
- Downloading up to 20,000 headers (about 1.6 MB) for an old transaction is bounded and cached.
- First sync after upgrading is slower for wallets with many cached proofs (re-verification once).

## References

- `src/Core/Spv/WalletSynchronizer.cs`
- `src/Core/Storage/WalletDocument.cs`
- `tests/PalladiumWallet.Tests/Spv/WalletSynchronizerTests.cs`
- Related: 0006, 0036
