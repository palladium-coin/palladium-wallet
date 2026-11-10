# 0060. Run the regtest end-to-end in CI, and exercise Donate on regtest

- **Status:** Accepted
- **Area:** Testing
- **Recorded:** 2026-11-10

## Context

`tests/regtest/e2e.sh` (ADR 0050, 0055) checks the wallet against a real Palladium Core and plm-fulcrum, but it ran only
by hand, so a regression it would catch could go unseen until someone ran it. And the Donate tab, which pays a fixed
mainnet address, could not be driven against a real node: only its guards and the broadcast reconciliation were tested.

## Decision

- CI has a `regtest-e2e` job on `ubuntu-latest` that runs the script (Docker is there; the images are public and pinned
  in the script).
- `MainWindowViewModel.DonationAddressOverride` (internal, like the other test seams) replaces the fixed address, so a
  regtest run pays a regtest one. The GUI regtest tests gain a donation that the node accepts; the script funds one coin
  per GUI test (four) and expects four transactions in the mempool.
- The script also covers the wrapped multisig of ADR 0058.

## Consequences

- A change that breaks the real node, the indexing server or the PSBT interoperability fails CI. The job adds the image
  pulls and a build to the run (about two minutes on top of the script's own time), and depends on ghcr.io being reachable.
- The override is never set outside tests; the shipped address is still the constant shown in the Donate tab.

## References

- `.github/workflows/ci.yml`, `tests/regtest/e2e.sh`, `tests/PalladiumWallet.Tests/App/RegtestGuiTests.cs`, `src/App/ViewModels/MainWindowViewModel.Donate.cs`
- ADR 0032, ADR 0050, ADR 0055
