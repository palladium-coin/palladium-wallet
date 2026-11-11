# 0052. Show the frozen total and the coin selection on every layout

- **Status:** Accepted
- **Area:** UI
- **Recorded:** 2026-11-10

## Context

ADR 0046 left two follow-ups open. A frozen coin leaves the balance, and nothing said how much was frozen, so a
forgotten freeze looked like missing funds. The coin-control summary line of the Send tab existed only in the desktop
layout; on a phone the user had to remember the selection made in the Coins tab, while a selection restricts the next
spend.

## Decision

- The balance card shows a line "Frozen coins: X" whenever the frozen total is not zero, in the same style as the
  pending, immature and verifying lines. It is computed in `ApplyCache` from the cached coins, which every freeze
  and every sync goes through, and is empty when nothing is frozen or no wallet is synced.
- The Send tab shows the coin-control summary in the phone layout as well, above the action buttons.

## Consequences

- A forgotten freeze is visible on the main screen. No new state: the total derives from the cache.
- The line adds a row to the balance card only while something is frozen.
- The decision of ADR 0046 is unchanged; this closes its two follow-ups for the GUI. The CLI side is ADR 0051.

## References

- `src/App/ViewModels/MainWindowViewModel.Receive.cs` (`FrozenText`, `ApplyCache`), `src/App/Views/MainView.axaml`
- `tests/PalladiumWallet.Tests/App/CoinControlTests.cs`
- ADR 0046, ADR 0051
