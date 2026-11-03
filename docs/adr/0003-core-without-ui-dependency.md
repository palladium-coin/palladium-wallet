# 0003. Layered architecture: Core knows nothing about the UI

- **Status:** Accepted
- **Area:** Architecture
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

The same wallet logic must be driven by a GUI, a CLI and headless tests. If cryptography or network code leaked into view code it could not be tested without a display and could not be audited in isolation.

## Decision

Layers depend only downward: GUI → wallet domain → SPV/sync → network → cryptography → persistence. `src/Core` (`Chain/ Crypto/ Wallet/ Spv/ Net/ Storage/`) has no UI dependency. `src/App` and `src/Cli` depend only on `Core`; the UI reaches the network and cryptography exclusively through the wallet domain.

## Consequences

- `dotnet test` covers almost all behaviour headlessly.
- The CLI is a second front end for free (0024).
- Violations (e.g. a ViewModel opening a socket) are review blockers, not style issues.

## References

- `src/Core`
- `src/Cli/Program.cs`
