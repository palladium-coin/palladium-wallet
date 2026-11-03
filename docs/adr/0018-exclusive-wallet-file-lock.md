# 0018. Exclusive OS-level lock on the open wallet file

- **Status:** Accepted
- **Area:** Storage
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Two processes opening the same wallet could overwrite each other's saves and corrupt state.

## Decision

`WalletLock.TryAcquire` holds a `FileStream` opened with `FileShare.None` on a companion `.lock` file for the session. The OS releases it on exit or crash, so there are no stale locks to clean up. A second opener gets `null`; permission errors propagate so the UI can show a distinct message.

## Consequences

- Safe multi-instance behaviour without lock-file housekeeping.
- Protects against concurrent app instances only, not against external tools editing the file.

## References

- `src/Core/Storage/WalletLock.cs`
