# 0041. Flag transactions above the newest checkpoint as not anchored

- **Status:** Accepted
- **Area:** SPV
- **Recorded:** 2026-11-03

## Context

ADR 0040 anchors every header used for a Merkle proof up to a hardcoded checkpoint at or above it. Blocks above the newest checkpoint (those mined since the last release) have nothing to anchor to, so for them only the Merkle proof against the header the server supplied is checked. This is a known limit; the user could not see which transactions it applied to.

## Decision

- `ChainProfile.IsAboveNewestCheckpoint(height)` is true for a confirmed height above the highest checkpoint of a profile that has checkpoints. It is false for unconfirmed transactions and for profiles without checkpoints (testnet/regtest), where nothing is anchored by design and the notion does not apply.
- `HistoryRow` carries `Unanchored`; the History list (desktop and mobile layouts) shows an orange "not anchored" label with a tooltip explaining it, and the transaction detail appends the same text to its status line.
- The flag is informational: it does not block spending or change the Merkle verification. Reducing the window itself is done by shipping a recent checkpoint with every release; a second-server cross-check or a bounded proof-of-work check remain open options (see the backlog).

## Consequences

- The reduced guarantee is visible per transaction instead of silent, and disappears after an update whose checkpoint covers the block.
- Every wallet user sees the label on the most recent transactions between releases; the tooltip and the user guide explain it. It can alarm users unnecessarily, which is the price of honesty here.

## References

- `src/Core/Chain/ChainProfile.cs`
- `src/App/ViewModels/MainWindowViewModel.Receive.cs`
- `src/App/Views/MainView.axaml`
- `tests/PalladiumWallet.Tests/App/MainWindowViewModelTests.cs`
- Related: 0040
