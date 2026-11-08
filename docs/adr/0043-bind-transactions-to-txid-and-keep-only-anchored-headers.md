# 0043. Bind transactions to their txid; persist only anchored headers; validate header ranges

- **Status:** Accepted
- **Area:** SPV
- **Recorded:** 2026-11-08

## Context

A logic audit of the SPV layer found three gaps that let a hostile or merely unlucky server break the guarantees of ADRs 0040 and 0042.

1. **The raw transaction was never tied to the txid it was requested under.** The Merkle proof proves that a *txid* is in a block; it never sees the transaction bytes. A server could answer `blockchain.transaction.get(abc…)` with a different, fabricated transaction and still pass the proof, producing a fake balance marked verified, hidden spends, or wrong input amounts (for legacy inputs the signature does not commit to the amount, so the difference becomes fee).
2. **Headers not backed by a checkpoint outlived the sync.** They were cached for the whole session and persisted to disk, including after a failed verification. After a shallow reorganisation the stale header made an honest server look like a forger ("not trustworthy"), the condition survived restarts, and a tx re-included at the same height in a different block kept its old verdict.
3. **A `blockchain.block.headers` reply was sliced without checking its shape.** A `count` larger than requested wrote unvalidated headers beyond the requested range into the cache; a short `hex` raised a non-SPV exception.

## Decision

- **Bind to the txid.** `VerifiedTransactions.Parse` / `ElectrumClient.GetVerifiedTransactionAsync` parse a raw transaction and require its txid (double SHA-256 of the non-witness serialisation) to equal the requested one, else `SpvVerificationException`. Used for every download in `WalletSynchronizer`, in `TransactionInspector` and in the CLI `send`. A cached transaction that does not hash to its key is dropped on preload and downloaded again.
- **Persist and trust only anchored headers.** A header is exported to disk, and accepted from disk, only if it lies in a segment proven to hash-chain up to a checkpoint (`AnchoredDownTo`). Headers above the newest checkpoint, and every header on profiles with no checkpoints, are never persisted. At the start of each sync the in-memory headers outside anchored segments, and the cross-check outcomes, are discarded.
- **Publish after validating.** An anchoring walk downloads the range, validates the checkpoint hash and the prev-hash chain, and only then writes the headers to the cache, all under one lock. A failed walk leaves nothing behind.
- **Renew recent verdicts.** A proof for a block above the newest checkpoint and less than `ReorgRecheckDepth` (24) blocks below the tip is verified again at every sync, so a block swapped at the same height is noticed. An inconclusive re-check removes the earlier verdict.
- **Validate range replies.** `count` must be between 1 and the number requested and `hex` must be exactly `count × 80` bytes; anything else, or non-hex content, is an `SpvVerificationException`.

## Consequences

- A server can no longer pass a forged transaction under a real txid; the only transaction it can serve for a txid is the real one.
- Honest servers are no longer reported as forgers after a shallow reorg, and the state does not survive a restart.
- Wallets re-fetch headers for blocks above the newest checkpoint instead of reading them from the cache; transactions deeper than 24 blocks keep their cached verdict, so a normal sync adds a few requests only for very recent history.
- Wallet files written by earlier versions keep working: their unanchored headers are ignored on load and rewritten without them at the next save.
- Not addressed: the txid excludes witness data, so a server can still alter signatures of a served transaction. This does not affect balances, history or amounts, which are what the wallet derives from it.

## References

- `src/Core/Spv/VerifiedTransactions.cs`
- `src/Core/Spv/WalletSynchronizer.cs`
- `src/Core/Wallet/TransactionInspector.cs`
- `tests/PalladiumWallet.Tests/Spv/WalletSynchronizerTests.cs`
- `tests/PalladiumWallet.Tests/Spv/VerifiedTransactionsTests.cs`
- Related: 0040, 0041, 0042
