# 0008. Progressive verification: show early, spend only after proof

- **Status:** Accepted
- **Area:** SPV
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

On wallets with thousands of historical transactions, blocking the whole sync on every Merkle proof made the app unusable, especially on mobile hardware.

## Decision

`WalletSynchronizer` raises `PartialResult` as soon as transaction downloads finish, then keeps verifying proofs in the background and refreshes the result periodically. Each cached transaction and UTXO carries a `Verified` flag. The UI marks unverified entries ("verifying…") and shows `PendingVerificationSats` separately. The invariant: `UtxoSpendability.IsSpendable` requires `Verified`, so unverified coins can be displayed but never spent. `SyncOnceAsync` still completes only when verification is finished, and the disk cache stores only the fully verified end state.

## Consequences

- Fast time-to-usable-balance.
- A forged balance can be displayed briefly but never spent before the proof failure aborts the sync.
- Balances have overlapping subsets (immature vs. pending verification); consumers must use `SpendableSats`, never subtract the subsets.

## References

- `src/Core/Spv/WalletSynchronizer.cs`
- `src/Core/Wallet/UtxoSpendability.cs`
- `SECURITY.md`
