# 0046. Freeze coins and select them by hand in a Coins tab

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-09

## Context

Coin selection already honours a frozen flag: `CachedUtxo.Frozen` excludes a coin from the
balance (`SyncCache.SpendableSats`) and from every spend (`UtxoSpendability.IsSpendable`,
`TransactionFactory`), and the flag survives a sync (`WalletSynchronizer.FrozenOutpoints`). But
nothing in the application could set it: the only way was to edit the wallet file by hand. The
documents disagreed about it: ADR 0031 said freezing "exists in the cache", the user guide said
"no coin control". `TransactionFactory` already accepts an explicit list of coins, which no
screen used.

Professional wallets (Sparrow, Electrum) show the coins of a wallet, let the user freeze any of
them, and let the user choose the coins of a spend. Those are the two missing pieces here.

## Decision

We add a **Coins** tab to the main screen. It lists every UTXO with amount, outpoint, address and
status (spendable, frozen, unconfirmed, confirming, immature coinbase, awaiting Merkle proof).

- **Freeze and unfreeze.** Each coin has a freeze button. A frozen coin is never spent, by
  automatic selection or by coin control. The balance is recomputed at once
  (`CoinControl.SetFrozen`), and the wallet file is saved, so the decision survives a restart.
- **Freezes survive syncs.** The synchronizer receives the frozen outpoints before it runs, and
  when a sync finishes the freezes as they are *then* are applied (`CoinControl.ApplyFrozen`), so
  a freeze made while a sync runs is not lost.
- **Coin control.** The user selects spendable coins. While a selection exists, the next send or
  donation may spend only the selected coins. A selection that holds a coin which can no longer be
  spent (for example after a reorganisation) blocks the spend with a message until the coin is
  deselected. A successful send or donation clears the selection.
- **Session state.** The coin selection is not written to the wallet file. Freezes are.
- The Send tab shows a one-line summary of an active selection. The Coins tab is the place to
  change it.

The CLI is not changed by this decision: it has no freeze or coin-selection commands yet.

## Consequences

- Easier: a user can keep a coin out of every spend, and can spend chosen coins only. Both are
  standard expectations for a wallet with a graphical interface.
- Harder: a frozen coin that is forgotten keeps its value out of the balance until it is unfrozen.
  The balance does not yet show how much is frozen. This is the main risk of the feature.
- The wallet file now holds a user decision (the freezes), which must be preserved by every code
  path that writes it. The sync path does this by design; a future write path must do the same.
- The Coins tab is a sixth tab, so the tab bar has six columns. The summary line in the Send tab
  is shown in the desktop layout only; the phone layout reads the selection in the Coins tab.
- Follow-up, not decided here: CLI commands to list, freeze and unfreeze coins (`lockunspent`
  is the model); a frozen total shown next to the balance.

## References

- `src/Core/Wallet/CoinControl.cs`: freeze, unfreeze and the balance recomputation.
- `src/Core/Spv/WalletSynchronizer.cs`: `FrozenOutpoints`, applied while UTXOs are built.
- `src/Core/Wallet/UtxoSpendability.cs`: the spendability gate that excludes frozen coins.
- `src/App/ViewModels/MainWindowViewModel.Coins.cs`: the Coins tab, the freeze and selection
  commands, and the spend pool used by send and donate.
- `src/App/Views/MainView.axaml`: the Coins tab.
- `tests/PalladiumWallet.Tests/App/CoinControlTests.cs` and `tests/PalladiumWallet.Tests/Wallet/CoinControlTests.cs`.
- ADR 0031 (scope): it describes freezing as existing in the cache; this ADR makes it reachable.
