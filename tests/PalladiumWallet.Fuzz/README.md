# Fuzzing

Fuzz targets over every parser that consumes **untrusted input**: server
responses (block headers, Merkle proofs, peer lists), wallet files (plaintext
document and encrypted container), and user-pasted text (mnemonics, SLIP-132
keys, addresses, amounts).

Each target encodes the parser's **error contract**: the exception types
documented as its failure mode are swallowed, anything else escaping is a
finding. Targets: `header` `merkle` `slip132` `bip39` `address` `coinamount`
`walletdoc` `encfile` `peers` `headerrange` `cosignerkey` `psbt` `multisigconfig` (see `FuzzTargets.cs` for each contract).

## Three ways to run

**1. Corpus replay — automatic.** The seed corpus (including regression inputs
for every crash found so far) replays through all targets on every
`dotnet test` run via `FuzzCorpusTests`, so fixed findings cannot come back.

**2. Built-in random smoke — no tooling.** Not coverage-guided, but catches
shallow contract violations in seconds:

```bash
dotnet run -- bip39 --random 50000        # target, iterations [, seed]
```

**3. Coverage-guided campaign — afl++.** The real thing; run it before a
release or after touching any parser:

```bash
apt install afl++
dotnet tool install --global SharpFuzz.CommandLine
./fuzz.sh header              # one target per campaign
./fuzz.sh peers -V 3600       # extra args go to afl-fuzz
```

`fuzz.sh` builds Release, instruments `PalladiumWallet.Core.dll` (and NBitcoin)
with SharpFuzz, and launches afl-fuzz with the target's seed corpus. Findings
land in `findings/<target>/crashes/`.

## Triage workflow

```bash
dotnet run -- <target> findings/<target>/crashes/<file>   # replay: full stack trace
```

Fix the parser (typed exception or graceful rejection — see the hardening
commits for `Bip39.TryParse`, `ElectrumApi.ParsePeers`, `EncryptedFile.Decrypt`
as examples), then add the input to `SeedCorpus` in `Program.cs` as a
`regression-*` seed and regenerate with `dotnet run -- --make-seeds Corpus`.

## Findings so far

The first smoke run found two real bugs, both reachable from untrusted input:

- `Bip39.TryParse` crashed with `NotSupportedException` on restore text
  resembling no wordlist (NBitcoin's `Wordlist.AutoDetect` throws for its
  internal "Unknown" language).
- `ElectrumApi.ParsePeers` crashed with `InvalidOperationException` on a peer
  list containing a JSON string with invalid UTF-8 bytes (parses fine,
  fails at `GetString()` transcoding) — attacker-controlled server data.

Plus two hardening changes made so the `encfile`/`peers` contracts could be
strict at all: `EncryptedFile.Decrypt` maps every malformed-container failure
to `InvalidDataException` and bounds the PBKDF2 iteration count (a tampered
file could previously demand 2^31 iterations, hanging the wallet at open), and
`ParsePeers` tolerates any JSON shape.
