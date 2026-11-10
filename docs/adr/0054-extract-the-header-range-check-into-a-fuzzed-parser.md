# 0054. Extract the header-range check into a fuzzed parser

- **Status:** Accepted
- **Area:** Testing
- **Recorded:** 2026-11-10

## Context

ADR 0043 made the synchronizer reject a `blockchain.block.headers` reply unless its count and hex length are
exactly what was requested. The check lived inline in a private async method that needs a connected client, so it
could be exercised only through `WalletSynchronizerTests`, and it was the one untrusted-input parser of the SPV code
that no fuzz target covered (ADR 0026). The open question was whether to move it into a parser.

## Decision

The shape check moves, unchanged, into `HeaderRange.Split(range, requested, startHeight)` in `Core/Spv`, which the
synchronizer calls. Its contract: for any input it returns exactly `Count` headers of 160 hex characters, with
`1 <= Count <= requested`, or throws `SpvVerificationException`; nothing else escapes. The checks run in an order that
never multiplies an out-of-range count (a count near `int.MaxValue` is rejected before `count * 160` is computed).

A fuzz target `headerrange` encodes that contract (input: count, requested, hex text). Its seed corpus holds the
boundary cases (zero, negative, more than requested, too short, overflowing count) and replays inside `dotnet test`;
CI runs it in the smoke matrix. `HeaderRangeTests` pin the same cases.

## Consequences

- The check is testable without a server and is fuzzed like every other parser of server data.
- Behaviour is unchanged: the same inputs are accepted and rejected, with the same messages.
- The hex characters are still validated later, when each header is parsed (`BlockHeaderInfo.Parse`).

## References

- `src/Core/Spv/HeaderRange.cs`, `src/Core/Spv/WalletSynchronizer.cs` (`FetchHeaderRangeAsync`)
- `tests/PalladiumWallet.Fuzz/FuzzTargets.cs`, `tests/PalladiumWallet.Tests/Spv/HeaderRangeTests.cs`
- ADR 0026, ADR 0043
