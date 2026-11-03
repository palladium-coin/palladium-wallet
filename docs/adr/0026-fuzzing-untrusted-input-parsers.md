# 0026. Fuzz every untrusted-input parser

- **Status:** Accepted
- **Area:** Testing
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Headers, Merkle branches, extended keys, mnemonics, addresses, amounts, wallet documents, encrypted files and peer lists all arrive from outside the trust boundary. A crash or unexpected exception there is a bug or a denial of service.

## Decision

`tests/PalladiumWallet.Fuzz` (SharpFuzz) has one target per parser (`header merkle slip132 bip39 address coinamount walletdoc encfile peers`), each encoding that parser's error contract: any other escaping exception is a finding. The seed corpus, including regression inputs for past findings, replays inside `dotnet test` through `FuzzCorpusTests`. Property-based tests (CsCheck) complement it for roundtrip invariants.

## Consequences

- Past findings (corrupted pin file, JSON wallet detection) became permanent regression inputs.
- A new parser requires a new fuzz target and seed entries.
- Coverage-guided campaigns need afl++, so they are manual rather than part of the default test run.

## References

- `tests/PalladiumWallet.Fuzz/Program.cs`
- `tests/PalladiumWallet.Fuzz/fuzz.sh`
