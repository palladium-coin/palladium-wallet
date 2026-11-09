# 0047. Reconcile a mismatched broadcast reply with the transaction the server holds

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-09

## Context

A broadcast returns a txid. The txid is fixed by the signed bytes, so the wallet knows the right
answer in advance, and a different one means the server is not reporting what was sent: it relayed
other bytes, it lies, or it has a bug. The first fix only turned that case into an error and kept
the pending send. That left the user without an answer to the question that matters: did the
payment leave or not? Clearing the pending transaction would be wrong too: if it did leave, the
user could build a second send, and the signed bytes would be gone.

Professional wallets do not take the reply of a broadcast as the state of a send. The state is what
the network holds, and a signed transaction stays available to be sent again.

## Decision

On a reply with another txid (`BroadcastTxidMismatchException`) the wallet trusts neither side and
asks the server for the transaction it built, by its own txid (`ElectrumApi.HasTransactionAsync`):

- **The server returns a transaction that hashes to our txid:** the transaction is in the network.
  The send is done (form, pending transaction and coin selection cleared, wallet resyncs), and the
  message names our txid and says the server replied with another one.
- **The server does not know it, or returns something else:** the transaction stays pending and the
  confirm button stays active. Sending it again is harmless, because the bytes are the same. The
  message says so.
- **The check cannot be completed (connection lost):** the transaction stays pending and the
  message says the network could not be checked.

The same rule applies to the GUI (send and donate) and to the CLI (`send --broadcast` and
`sign-psbt --broadcast`). The CLI exits 0 when the transaction is in the network, and 1 otherwise,
and in the second case it prints the signed hex so it can be sent again.

A transport failure is never turned into an answer: `HasTransactionAsync` returns false only for a
JSON-RPC error or an unreadable transaction, and lets connection errors propagate.

## Consequences

- The user is told whether the payment left, instead of being given a bare error.
- After a mismatch the signed transaction is never discarded automatically. It is cleared when
  the send is confirmed in the network, or replaced when the user prepares another send.
- The answer to the check comes from the same server that already gave a wrong reply, so it is
  evidence, not proof. A send is certain only once a sync shows it in the wallet history, which
  the wallet does right after it. This limit is stated in `SECURITY.md`.
- One extra request, only in the mismatch case.
- Not decided here: an explicit "cancel this send" button. Today a new Prepare replaces the pending
  transaction.

## References

- `src/Core/Net/ElectrumApi.cs`: `BroadcastVerifiedAsync`, `HasTransactionAsync`.
- `src/App/ViewModels/MainWindowViewModel.Send.cs`: `ConfirmSend` and `ReconcileBroadcastMismatchAsync`.
- `src/App/ViewModels/MainWindowViewModel.Donate.cs`: `ConfirmDonate`.
- `src/Cli/Program.cs`: `BroadcastSigned`.
- `tests/PalladiumWallet.Tests/Net/BroadcastVerificationTests.cs`,
  `tests/PalladiumWallet.Tests/App/BroadcastReconciliationTests.cs`,
  `tests/PalladiumWallet.Tests/App/CliTests.cs`.
