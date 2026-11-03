# 0036. Verify the chain identity of a server right after connecting

- **Status:** Accepted
- **Area:** SPV
- **Recorded:** 2026-11-03

## Context

The wallet connected to any Electrum server that answered on the configured port and found out it was on another chain only indirectly, and only for transactions at or above the first checkpoint, with a message that did not name the cause. The server probe checked the genesis hash; the wallet did not. A further complication: the mainnet profile reuses Bitcoin's genesis hash, and registry servers report exactly that value, so a genesis check alone cannot tell Palladium from Bitcoin.

## Decision

After the handshake, `ChainVerifier.VerifyChainAsync` (`Core/Spv/ChainVerifier.cs`) runs once per connection:

1. `server.features`: `genesis_hash` must be present and equal the profile's genesis (case-insensitive). A server that does not report it, or errors on the method, is rejected: the protocol requires it, and on testnet/regtest it is the only identity check.
2. `blockchain.headers.subscribe` gives the tip height. The highest hardcoded checkpoint with `Height <= tip` is selected.
3. The header at that height (`blockchain.block.header`) is hashed and compared with the checkpoint. A mismatch, a server error, or a malformed header means a different chain.

Any failure throws `WrongChainException` (server, reason). The GUI (connect and peer discovery), the CLI and the server probe all call the same helper; the GUI disposes the connection and moves on to the next candidate server, showing a localised message if none is usable.

A tip **below the first checkpoint** (a young testnet, a server still syncing) has nothing to compare and is accepted on the genesis check alone. A tip below the *latest* checkpoint is checked against the highest one it has reached; being behind is not an error.

## Consequences

- A server on another chain or fork is refused at connect time with an explicit message instead of failing indirectly during a sync.
- Mainnet is protected by the checkpoint header, not by the Bitcoin-shared genesis. Testnet and regtest (no checkpoints yet) rely on the genesis alone.
- Two extra requests per connection (`server.features`, `blockchain.headers.subscribe`, plus one header on mainnet). The sync asks for the tip again anyway.
- The check is made once, at connect time; a server that changes chain mid-connection is still caught by the per-transaction anchoring.

## References

- `src/Core/Spv/ChainVerifier.cs`
- `tests/PalladiumWallet.Tests/Spv/ChainVerifierTests.cs`
- `tests/PalladiumWallet.ServerProbe/Program.cs`
- Related: 0009, 0034
