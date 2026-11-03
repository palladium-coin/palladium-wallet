# 0042. Cross-check recent headers with a witness server; no bounded-PoW check

- **Status:** Accepted
- **Area:** SPV
- **Recorded:** 2026-11-03

## Context

ADR 0040 anchors every header used for a Merkle proof up to a hardcoded checkpoint, but a block above the newest checkpoint has nothing to anchor to: only the header the primary server supplies and the Merkle proof against it are checked. A hostile server could show a payment that never happened in those recent blocks. ADR 0041 made the limit visible; this ADR reduces it.

## Decision

- **Witness cross-check.** For a block above the newest checkpoint, `WalletSynchronizer` asks a second server (a "witness") for the header at the same height and requires identical bytes. The witnesses come from `WalletSynchronizer.WitnessProvider`, called lazily once, only when such a header is first needed.
- **Witness selection** (`WitnessConnector`): only from the **hardcoded bootstrap list** of the profile, never from the peers a server announces (a hostile server could announce only its own), never the primary server, each verified to be on the chain (ADR 0036). One witness by default (`DefaultMax`). Used by the GUI and the CLI; the GUI closes them with the primary connection.
- **Outcomes.** Agreement: the proof verifies as before. A witness that is unreachable, errors, or does not know the block: skipped. No witness at all: unchanged behaviour (Merkle proof only). Disagreement at a depth of at least `CrossCheckMinDepth` (3) blocks: `SpvVerificationException` ("servers disagree"), the sync aborts. Disagreement at a shallower depth may be a reorganisation in progress: the proof is left **unverified** (not spendable, not cached) and decided again at the next sync.
- Each height is asked once per synchronizer. Heights covered by a checkpoint are not cross-checked.
- The "not anchored" flag of ADR 0041 stays height-based: it is not cleared by a successful cross-check, because that result is not persisted.

## Rejected: bounded proof-of-work

The idea was to require recent headers to meet a target no easier than a bound on the newest checkpoint's difficulty. The data in the shipped checkpoints argue against it: between consecutive checkpoints (20,000 blocks) the target changed by up to 8x easier and 200x harder, so a safe bound would be loose; the network is small (about tens of TH/s at the last checkpoint), so work at a fraction of a block's difficulty is cheap to rent; and a bound that is too tight would make the wallet reject honest servers whenever hash rate drops. It would add cost and a false-rejection risk for little protection. Not implemented.

## Consequences

- A single lying server can no longer fabricate a recent payment on its own: it would have to control the witness too. A witness run by the same operator as the primary, or an attacker positioned in front of both, defeats it; this is a hardening, not a proof.
- One extra connection (TLS, pinned) is opened when a recent block needs checking; none for wallets whose history is all below the newest checkpoint.
- A transaction in a block 1-2 deep on which the servers briefly disagree shows as "verifying" until the next sync.
- A fresh checkpoint in every release still shrinks the window the cross-check has to cover.

## References

- `src/Core/Spv/WalletSynchronizer.cs`
- `src/Core/Spv/WitnessConnector.cs`
- `tests/PalladiumWallet.Tests/Spv/WalletSynchronizerTests.cs`
- Related: 0036, 0040, 0041
