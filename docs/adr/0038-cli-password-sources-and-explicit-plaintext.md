# 0038. CLI password sources and explicit plaintext storage

- **Status:** Accepted
- **Area:** Distribution
- **Recorded:** 2026-11-03

## Context

The CLI read the wallet password only from `--password`, where it is stored in the shell history and visible to other local users in the process list (ADR 0024 already noted it). Worse, when `--password` was missing in `create`/`restore*` the CLI printed a warning and stored the **seed in plaintext**: a forgotten flag silently produced an unencrypted wallet.

## Decision

- Password sources, one at most per command: `--password-env NAME`, `--password-stdin` (first line), or `--password P` (kept for compatibility, prints a warning on stderr). With none, a no-echo prompt on the terminal (`Console.ReadKey(intercept: true)`), with a confirmation when creating a wallet. With no terminal the command fails with a clear message and never hangs.
- A new wallet (`create`, `restore`, `restore-xpub`, `restore-address`) is stored unencrypted **only** with `--no-encryption`, which prints a warning and cannot be combined with a password option. An empty password is refused.
- Opening: a plaintext wallet needs no password; an encrypted one needs a source. The password used to open is reused to save the wallet back (`sync`).
- Implemented in `src/Cli/CliPassword.cs`; the command logic in `Program.cs` only asks it for the password.

## Consequences

- A forgotten flag can no longer produce a plaintext wallet; scripts that relied on that must add `--no-encryption` (recorded as a behaviour change in the changelog).
- `--password` is not removed, so existing scripts with a password keep working; removing it is left to a major version.
- The environment variable route still exposes the password to processes that can read the environment of the CLI; it is safer than an argument but not a hardened interface (the CLI remains a developer tool).

## References

- `src/Cli/CliPassword.cs`
- `src/Cli/Program.cs`
- `tests/PalladiumWallet.Tests/App/CliTests.cs`
- Related: 0024
