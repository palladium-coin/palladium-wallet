# 0019. Resolve the data root by explicit precedence

- **Status:** Accepted
- **Area:** Storage
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Desktop users want portable installs or a custom folder; Android must use its sandbox; existing installs must keep their data.

## Decision

`AppPaths.DataRoot()` resolves in order: `OverrideDataRoot` (set by the Android head to the app sandbox, or by tests), portable `palladium-data` folder next to the executable, user-chosen folder stored in a pointer file at a fixed bootstrap location, legacy location if it already holds data, then the platform default (`%APPDATA%\PalladiumWallet` on Windows, `~/.palladium-wallet` on Linux). Each network gets its own subfolder (config, wallets, certificates, servers).

## Consequences

- First-run wizard asks for a location only when none is determined; Android skips that step.
- Static override seams complicate tests, which therefore share the xUnit collection `AppPaths`.

## References

- `src/Core/Storage/AppPaths.cs`
- `src/App.Android`
