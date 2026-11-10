# 0061. Exchange PSBT files and the multisig configuration with other tools

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-10

## Context

PSBT was copied as base64 text only, and a multisig wallet could be built only by typing its keys. Hardware signers read
and write binary `.psbt` files on an SD card and register a multisig wallet from a text configuration file (Coldcard's
format, which Sparrow also exports); Sparrow imports output descriptors. None of that could be tried here against the
real devices or programs, so what is implemented is the file formats and what can be checked without them.

## Decision

- `PsbtFormat` reads a PSBT as a binary file (`psbt` + `0xff`), base64 or hex, and writes the binary form. Anything else
  is a `FormatException`; a file over 4 MiB is refused. The CLI's `sign-psbt` and `combine-psbt` take a file path, text or
  `-`; `send`, `sign-psbt` and `combine-psbt` accept `--psbt-out FILE` to also write the binary file.
- `MultisigConfig` writes and reads the configuration text (`Name`, `Policy: M of N`, `Format: P2WSH | P2SH-P2WSH`, then
  `Derivation:` blocks of `FINGERPRINT: xpub` lines) and prints the receive and change descriptors with checksums.
  `export-multisig` exports them; `create-multisig --config FILE` builds the wallet from such a file (the wallet ID
  and `--expect-id` apply as ever, ADR 0057). The reader refuses anything that would change the wallet silently: a
  policy out of range, a key count that differs from the policy, a key that is not a cosigner key of the network.
- Both readers are untrusted-input parsers, so each has a fuzz target (`psbt`, `multisigconfig`). The first run found
  that NBitcoin throws `InvalidDataException` on a non-canonical varint in a binary PSBT; it is now a `FormatException`,
  with the input in the seed corpus.

## Consequences

- A PSBT can go to a signer's SD card and come back, and a configuration file can be handed to a signer or another
  wallet. The round trips and the formats are tested, and the descriptors' checksums match Palladium Core's.
- Not verified: any real hardware signer, Sparrow or Electrum. The formats follow what those tools document; a quirk of
  one of them can still show up on first contact. Signers also need the multisig registered on the device, which is
  done on the device with the exported file, not by this wallet.
- Not done: QR / animated-QR transport, and importing a configuration in the GUI.

## References

- `src/Core/Wallet/PsbtFormat.cs`, `src/Core/Wallet/MultisigConfig.cs`, `src/Cli/Program.cs`
- `tests/PalladiumWallet.Tests/Wallet/PsbtFormatTests.cs`, `MultisigConfigTests.cs`, `App/CliTests.cs`, `tests/PalladiumWallet.Fuzz/`
- ADR 0056, ADR 0057, ADR 0058; BIP174
