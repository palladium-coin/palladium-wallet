# 0050. Sync before every CLI send and keep sign-psbt offline

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-10

## Context

`send` builds a transaction from the cached UTXOs. A cache older than the last spend would select an output that is
already spent (a double spend the server refuses at broadcast), so `send` syncs first. The question was whether to
keep that unconditional, add a `--no-sync`, or sync only when the cache looks stale; and whether `sign-psbt` should sync.

Electrum and Sparrow never sync "before a send": they stay connected and subscribed to every address, so their state is
current when the user sends. The GUI does the same here (`MainWindowViewModel.Sync.cs` handles
`blockchain.scripthash.subscribe` and `blockchain.headers.subscribe` notifications). The CLI has no resident process, so
its equivalent of "being current" is one sync per invocation.

Cost, measured on a regtest wallet with a warm cache: a full `sync` takes about 1.2 s including .NET start-up.
`WalletSynchronizer` reuses the cached transactions and the Merkle proofs already verified (`PreloadCaches`), so only
what changed is downloaded and proven.

## Decision

- `send` always syncs before building. There is no `--no-sync`: it would make the unsafe path one flag away.
  "Sync only if the cache is stale" is rejected because the chain tip is known only to the server, so the check
  already needs the connection.
- `sign-psbt` does not sync. It is the offline half of the watch-only and air-gapped flow and signs what the PSBT
  states; the network is used only for `--broadcast`. An input without its previous transaction is already reported
  as "amount not verified", so the shown fee is not presented as reliable in that case.

## Consequences

- Every `send` costs one connection and an incremental sync; cheap with a warm cache, longer on a first run.
- Offline signing stays possible through PSBT.
- If the sync ever becomes a bottleneck on large wallets, optimise the proof re-verification (blocks above the newest
  checkpoint are re-checked at every sync, ADR 0040) rather than relaxing the policy.

## References

- `src/Cli/Program.cs` (`Send`, `SyncWallet`, `SignPsbt`)
- `src/Core/Spv/WalletSynchronizer.cs`, `src/App/ViewModels/MainWindowViewModel.Sync.cs`
- ADR 0040
