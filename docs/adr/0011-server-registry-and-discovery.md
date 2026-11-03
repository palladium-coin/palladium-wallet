# 0011. Server registry seeded by bootstrap list and peer discovery

- **Status:** Accepted
- **Area:** Network
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

The wallet needs a server without hardcoding a single point of failure, and users should be able to pick their own.

## Decision

`ServerRegistry` starts from the profile's bootstrap servers, adds peers announced via `server.peers.subscribe` (missing ports fall back to profile defaults) and persists the list per network. The first entry is the default when the user specifies none. The last successfully connected server is stored in `AppConfig`.

## Consequences

- Works out of the box and survives bootstrap servers disappearing.
- There is no multi-server pooling or cross-checking yet (listed as a limitation in `SECURITY.md`).
- Discovered peers are unauthenticated hints; every response is still verified (0007).

## References

- `src/Core/Net/ServerRegistry.cs`
- `src/Core/Storage/AppConfig.cs`
