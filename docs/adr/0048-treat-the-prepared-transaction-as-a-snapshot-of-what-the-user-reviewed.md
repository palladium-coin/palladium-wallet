# 0048. Treat the prepared transaction as a snapshot of what the user reviewed

- **Status:** Accepted
- **Area:** UI
- **Recorded:** 2026-11-09

## Context

Send is two steps: Prepare builds and signs the transaction, Confirm broadcasts it. Confirm sent the
transaction built by the last Prepare, but nothing tied that transaction to the form:

- editing the destination, the amount, *Send all* or the fee after Prepare left Confirm active, so
  it sent the old transaction while the form showed new values;
- the review showed a truncated txid and the fee, not who is paid and how much, so the user could not
  check the destination against the transaction;
- a prepared transaction could outlive its coins: a sync could find them spent elsewhere, back in the
  mempool after a reorganisation, or frozen (ADR 0046), and Confirm would still try to send it.

ADR 0047 also left open whether a "cancel this send" button was needed.

Professional wallets treat the confirmation as an immutable snapshot: what is approved is exactly
what is sent (Electrum shows the built transaction in a modal; Sparrow builds it in its own view and
a changed form needs a new build).

## Decision

- **The review is read from the built transaction.** It shows the amount and the full address the
  transaction pays, the change that returns to the wallet, then the txid and the fee. If the built
  transaction does not pay the requested address, there is nothing to confirm and Prepare fails
  with an error.
- **Anything that went into the transaction discards it when it changes**, and clears the review:
  the destination, the amount, *Send all*, the fee rate and the coin selection. The fee rate and the
  coin selection are shared with Donate, so they discard a prepared donation too; the donation amount
  discards it on its own.
- **A sync can invalidate it.** At the end of a sync, and after a coin is frozen, a prepared
  transaction with an input that is no longer spendable is dropped with a message. Only the final
  state of a sync is used: a partial snapshot may still show coins as unverified.
- **Prepare starts by discarding the previous transaction**, so a Prepare that fails never leaves the
  old one confirmable.
- **No cancel button.** Changing any field abandons the prepared transaction, and that is the way out.

## Consequences

- What Confirm sends is what the user reviewed, and the destination can be checked in full.
- Late fee estimates from the server change the fee field and so discard a prepared transaction; that
  is correct, because the fee shown would no longer be the form's.
- After a mismatched broadcast reply (ADR 0047) the transaction stays pending, since nothing in the
  form changed; the user can send it again, or change a field to abandon it.
- The prepared watch-only PSBT is discarded in the same way, so an exported PSBT always matches the
  summary on screen.
- Risk: the invalidation list must follow the inputs of the build. A new input of the transaction (a
  new field, a new option) must be added to it, or it will not discard the transaction.

## References

- `src/App/ViewModels/MainWindowViewModel.Send.cs`: the hooks, `DescribePayment`, `DiscardStalePending`.
- `src/App/ViewModels/MainWindowViewModel.Donate.cs`: the same rule for the donation.
- `src/App/ViewModels/MainWindowViewModel.Coins.cs`: the coin selection and freeze hooks.
- `src/App/ViewModels/MainWindowViewModel.Sync.cs`: the stale check at the end of a sync.
- `tests/PalladiumWallet.Tests/App/PreparedTransactionSnapshotTests.cs`.
- ADR 0046 (coins), ADR 0047 (mismatched broadcast reply).
