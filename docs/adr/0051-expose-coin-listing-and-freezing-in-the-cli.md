# 0051. Expose coin listing and freezing in the CLI

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-10

## Context

The Coins tab (ADR 0046) lets the GUI user freeze a coin, but the CLI could neither show the coins nor freeze
one, although the flag lives in the wallet file both share (`CachedUtxo.Frozen`, applied by `CoinControl`).
A script or a user without the GUI could not keep a coin out of a spend. Bitcoin Core offers `listunspent` and
`lockunspent` for this.

## Decision

Three offline commands over the cache of the last sync: `utxos` lists each coin with confirmations and state and the
frozen total; `freeze <txid>:<vout>` and `unfreeze <txid>:<vout>` set the flag through `CoinControl.SetFrozen` and
save the wallet (encrypted again when it was). They never connect: the coins are those of the last `sync`, and the
command says so. A malformed outpoint, or one the wallet does not have, is a clean error that changes nothing.

`send` syncs first (ADR 0050) and re-applies the stored flags (`SyncCache.FrozenOutpoints`), so a frozen coin
stays out of the spend.

## Consequences

- The CLI and the GUI act on the same flag; a coin frozen in one is frozen in the other.
- Freezing needs a prior sync to know the coin; a coin that has not been synced cannot be frozen yet.
- No manual selection of coins to spend from the CLI (only the GUI has it); out of scope here.

## References

- `src/Cli/Program.cs` (`Utxos`, `Freeze`), `src/Core/Wallet/CoinControl.cs`
- `tests/PalladiumWallet.Tests/App/CliTests.cs`
- ADR 0046, ADR 0050
