# 0049. Keep the dust relay rate in the chain profile, verified against the node

- **Status:** Accepted
- **Area:** Chain
- **Recorded:** 2026-11-10

## Context

A payment output below the dust threshold is refused by the nodes' relay policy (not by consensus), so the wallet
refuses it before signing. The threshold is the dust relay fee times the cost of creating and spending the output.
It was a literal `3` inside `TransactionFactory`, assumed to match Bitcoin Core. Sparrow and Electrum also hardcode
Core's constants and do not expose the value to the user.

The assumption was checked against Palladium Core v2.1.0: `DUST_RELAY_TX_FEE` is 3000 sat/kvB in `policy/policy.h` of the node source,
and on a regtest node started with `-acceptnonstdtxn=0` (regtest accepts dust by default) `testmempoolaccept` refused
the value just below each threshold and accepted the threshold: P2PKH 545/546, P2SH 539/540, P2WPKH 293/294.

## Decision

`ChainProfile.DustRelaySatPerVByte` (3 for every network) holds the rate, next to the other chain constants;
`TransactionFactory.DustLimitSats(script, profile)` derives the per-script threshold from it. It is not a user setting:
it is a property of the network's nodes, and a too-low value only produces transactions the network refuses.

## Consequences

- A fork with another relay policy changes one line in `ChainProfiles`.
- The value is still an assumption about the nodes the user reaches; a node with a stricter policy refuses the
  broadcast and the user sees the server's error.
- Golden thresholds are pinned in `TransactionFactoryLimitsTests`.

## References

- `src/Core/Chain/ChainProfile.cs`, `src/Core/Wallet/TransactionFactory.cs` (`DustLimitSats`)
- `tests/PalladiumWallet.Tests/Wallet/TransactionFactoryLimitsTests.cs`
