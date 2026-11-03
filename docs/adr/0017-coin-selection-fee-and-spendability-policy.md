# 0017. Coin selection, fee and confirmation policy

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Fund safety depends on never spending immature or unverified coins and on producing standard, relayable transactions.

## Decision

`TransactionFactory` supports automatic or manual UTXO selection, a fixed fee rate in sat/vByte, send-all with the fee subtracted, change on the internal chain, and RBF enabled on every transaction. It rejects transactions above 100,000 vbytes. `UtxoSpendability.IsSpendable` is the single gate shared by coin selection and balance reporting: a coinbase output needs maturity + 1 confirmations (121 on mainnet), a regular output needs `MinConfirmations` (policy, not consensus), frozen UTXOs are excluded, and the Merkle proof must be verified (0008).

## Consequences

- Balances and coin selection cannot disagree.
- Users may see "insufficient funds" despite a larger visible balance when part of it is immature or unverified.
- RBF is set but fee bumping is not exposed in the UI (see `SECURITY.md`).

## References

- `src/Core/Wallet/TransactionFactory.cs`
- `src/Core/Wallet/UtxoSpendability.cs`
