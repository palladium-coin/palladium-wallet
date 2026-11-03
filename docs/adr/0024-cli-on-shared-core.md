# 0024. A CLI front end on the same Core

- **Status:** Accepted
- **Area:** Distribution
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Scripting, headless servers and quick manual verification need the wallet without a GUI.

## Decision

`src/Cli` exposes `create`, `restore`, `restore-xpub`, `restore-address`, `info`, `sync`, `send`, `servers`, `reset-certs`, `newseed` and `addresses` on the same `Core` as the GUI, with `--net`, `--file`, `--server host:port` and `--ssl` options. The default wallet file is `~/.palladium-wallet/<network>/wallets/default.wallet.json`.

## Consequences

- Core behaviour is exercised outside the GUI.
- Passwords on the command line are visible to other local users and shell history; the CLI is a developer tool, not a hardened interface.

## References

- `src/Cli/Program.cs`
