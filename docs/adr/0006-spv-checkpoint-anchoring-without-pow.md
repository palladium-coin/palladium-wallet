# 0006. Anchor headers to hardcoded checkpoints and skip PoW validation

- **Status:** Accepted (the anchoring direction is superseded by [0040](0040-anchor-headers-up-to-a-checkpoint.md))
- **Area:** SPV
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Palladium uses LWMA difficulty with 2-minute blocks. An SPV client cannot recompute LWMA without the full header history, and NBitcoin assumes Bitcoin's retargeting rules, so checking proof of work client-side would either be wrong or require downloading every header.

## Decision

Set `SkipPowValidation = true` in the profile and anchor trust to hardcoded checkpoints instead. For each header used in a Merkle proof, `WalletSynchronizer.AnchorToCheckpointAsync` downloads the intervening headers, verifies the prev-hash chain back to the nearest checkpoint at or below that height, and requires that checkpoint's hash to match exactly. Mainnet carries a checkpoint every 20,000 blocks.

## Consequences

- A server cannot feed an alternative history below the newest checkpoint.
- Headers above the newest checkpoint are linked but their work is not verified; this is an accepted limitation documented in `SECURITY.md`.
- Testnet/regtest have no checkpoints, so headers there are trusted at face value.
- Checkpoints must be extended with releases; a wrong checkpoint would reject every honest server.

## References

- `src/Core/Spv/WalletSynchronizer.cs`
- `src/Core/Spv/BlockHeaderInfo.cs`
- `src/Core/Chain/ChainProfiles.cs`
- `SECURITY.md`
