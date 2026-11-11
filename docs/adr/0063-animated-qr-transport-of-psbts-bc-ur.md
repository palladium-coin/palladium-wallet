# 0063. Animated-QR transport of PSBTs (BC-UR)

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-11

## Context

Air-gapped signers (Keystone, Passport, Jade, SeedSigner, Sparrow's QR mode) exchange PSBTs as animated QR codes in the
Blockchain Commons UR format; a PSBT is far too large for one QR. ADR 0061 and ADR 0062 left this undone: PSBTs travelled
as files and text only, and only the cosigner key was shown as a QR.

## Decision

- **Codec in `Core/Wallet/Ur`**, hand-written (NBitcoin has none): bytewords in the minimal style with CRC-32 (BCR-2020-012),
  the CBOR subset a UR needs (unsigned integers, byte strings, definite arrays, shortest form only), and the fountain code
  of BCR-2020-005 (xoshiro256** seeded from SHA-256 of part number and checksum, weighted degree choice through a Walker
  alias table, peeling decoder).
- **Type** `crypto-psbt` is emitted (the one the signers above read); `psbt` is accepted too on input.
- **Encoder**: a payload that fits one fragment is a single frame; otherwise an endless cycle of parts (100-byte fragments),
  so the reader may start anywhere and lose frames.
- **Decoder treats every frame as hostile**: message size capped at 4 MiB and part count at 20 000, the fragment length
  must be consistent with the sender's split, frames of another message are refused, repeats are harmless, and a rebuilt
  message is kept only if its CRC-32 matches (otherwise the collection restarts). `Receive` never throws.
- **GUI**: *Show as animated QR* on the pending PSBT (Send tab) and on the signed result (PSBT overlay) opens an overlay that
  cycles the frames at ~3 per second in QR alphanumeric mode (uppercase, error correction L). On Android the QR scanner
  recognises `ur:` codes, keeps scanning until the message is whole, shows the progress and returns the PSBT as base64
  into the PSBT box; *Scan QR…* in that overlay starts it. A scanned PSBT goes through the same review and signing path as a
  pasted one, so nothing is signed unseen.

## Consequences

- The sign-on-a-signer round trip needs no SD card or cable.
- The frames are checked against an independent implementation (foundation-ur-py, the Python port of the Blockchain
  Commons reference): for four payloads of 40 to 1000 bytes our encoder prints byte-for-byte the same frames, mixed parts
  included, and our decoder rebuilds the payload from them (`UrReferenceVectorTests`). Not tried with a real device.
- Desktop shows but does not scan (no camera access); the Android scanner change was not compiled or run in this environment.
- Fuzz target `ur` covers the decoder.

## References

- `src/Core/Wallet/Ur/`, `src/App/ViewModels/MainWindowViewModel.UrQr.cs`, `src/App.Android/ScannerActivity.cs`
- `tests/PalladiumWallet.Tests/Wallet/UrTests.cs`, `UrReferenceVectorTests.cs`, `tests/PalladiumWallet.Fuzz/Corpus/ur/`
- ADR 0061, ADR 0062; BCR-2020-005, BCR-2020-006, BCR-2020-012
