# 0004. Centralize all chain constants in a network profile

- **Status:** Accepted
- **Area:** Chain
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Palladium is a Bitcoin-derived chain with its own address prefixes, BIP32 headers, bech32 HRP, genesis, ports and coin type, and it runs on mainnet, testnet and regtest. Scattered magic numbers make a wrong-network address or key a silent fund-safety bug.

## Decision

Define every chain constant once in `Core/Chain` (`ChainProfile`, `ChainProfiles`, `PalladiumNetworks`): address prefixes (P2PKH 55, P2SH 5), bech32 HRP `plm`, SLIP-132 headers, genesis hash, default ports (50001/50002), BIP44 coin type 746, maturity and confirmation policy, bootstrap servers and checkpoints. The profile is selected per network and passed into the code that needs it.

## Consequences

- A new network or parameter change is a one-file edit.
- Testnet and regtest ship with empty bootstrap-server and checkpoint lists (TODO in code), so they rely on a user-supplied server and have no checkpoint anchoring (see 0006).
- Mainnet currently reuses Bitcoin's genesis hash, as noted in the profile.

## References

- `src/Core/Chain/ChainProfile.cs`
- `src/Core/Chain/ChainProfiles.cs`
- `src/Core/Chain/PalladiumNetworks.cs`
