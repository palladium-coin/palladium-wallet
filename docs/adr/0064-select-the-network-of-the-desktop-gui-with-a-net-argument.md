# 0064. Select the network of the desktop GUI with a `--net` argument

- **Status:** Accepted
- **Area:** UI
- **Recorded:** 2026-11-11

## Context

The GUI ran on mainnet only: the network was fixed in `MainWindowViewModel` and only a test seam (`ProfileOverride`)
could change it. Trying a feature by hand (multisig, PSBT, coin control) therefore meant doing it on real funds or with
the CLI, which has had `--net mainnet|testnet|regtest` since the beginning. The regtest end-to-end (ADR 0055, ADR 0060)
already drives the view model against a real node, but a person cannot look at the screen it produces.

## Decision

- **`--net mainnet|testnet|regtest` (or `--net=NAME`) on the desktop head**, read in `src/App.Desktop/Program.cs` by
  `StartupOptions.TryParse` (a public static class in the `App` library, so `ProfileOverride` stays `internal`).
  Without the argument the network is mainnet, unchanged. A missing or unknown name (case-sensitive, no normalisation)
  prints a message and exits with code 2 instead of falling back to any network.
- **No environment variable and no persisted setting.** A variable left in the shell, or a remembered choice, would open a
  test network (or, worse, mainnet) without the user noticing; an argument has to be typed on every start.
- **The window title carries the network** on testnet and regtest (`Palladium Wallet 1.2.0 [REGTEST]`); mainnet keeps the
  plain title. Wallets, servers and the config of each network already live in their own folders (`AppPaths`), so a test
  session never touches mainnet files.
- Testnet and regtest ship without bootstrap servers or checkpoints (ADR 0049's profile); the server is typed in the
  server settings (`127.0.0.1:50111` for the local stack).
- **`tests/regtest/up.sh` / `down.sh`** start and remove a throw-away node + plm-fulcrum (130 blocks mined, names
  `plm-gui-regtest-*`, separate from the `plm-e2e-*` containers of `e2e.sh`) and print how to fund a GUI address.
- Android is unchanged: it has no command line and stays on mainnet.

## Consequences

- The shipped default cannot reach a test network by accident, and a test network cannot be mistaken for mainnet on screen.
- The checkpoints of a test network are empty, so SPV anchoring (ADR 0040) does not apply there; this is a place to try
  the flows, not to judge the header validation.
- `StartupOptions.Net` is static state: tests that touch it run in xUnit collection `AppPaths` and `VmTestBase` resets it.

## References

- `src/App/StartupOptions.cs`, `src/App.Desktop/Program.cs`, `src/App/ViewModels/MainWindowViewModel.cs`
- `tests/PalladiumWallet.Tests/App/StartupOptionsTests.cs`, `tests/regtest/up.sh`, `tests/regtest/down.sh`
- ADR 0049, ADR 0055, ADR 0060
