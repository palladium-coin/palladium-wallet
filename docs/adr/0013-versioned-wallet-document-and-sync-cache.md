# 0013. Versioned JSON wallet document with an offline sync cache

- **Status:** Accepted
- **Area:** Storage
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Wallet metadata (labels, contacts, gap limit) and the last synced state must persist across sessions and across app upgrades; balances should be viewable offline.

## Decision

`WalletDocument` is a versioned JSON schema (`CurrentVersion = 1`) with a migration hook on open. It holds the seed or keys (so it must be encrypted, 0012), account xpub and path, labels, contacts, gap limit (default 20) and a `SyncCache` with balance/history. Only confirmed transactions are cached because unconfirmed ones can change (RBF); only the fully verified end state of a sync is persisted (0008).

## Consequences

- Reopening a wallet is fast and works offline.
- Schema changes require a version bump and a migration, tested before release.
- A large cache makes saves heavy, so `WalletStore.SaveAsync` moves serialization and I/O off the UI thread.

## References

- `src/Core/Storage/WalletDocument.cs`
- `src/Core/Storage/WalletStore.cs`
