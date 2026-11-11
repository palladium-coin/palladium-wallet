# 0062. Sign PSBTs and exchange multisig files in the GUI

- **Status:** Accepted
- **Area:** UI
- **Recorded:** 2026-11-10

## Context

The GUI could build a PSBT and copy it (ADR 0056), and create a multisig wallet (ADR 0059), but a cosigner could not
sign the PSBT it received: only the CLI's `sign-psbt` did. The file formats of ADR 0061 were CLI-only, and the creation
flow lacked the seed passphrase, an account number and a confirmation of a generated seed.

## Decision

- **Sign a PSBT overlay** (button *Sign a PSBT…* on the Send tab, both layouts): paste base64 or hex, or open a file
  (binary `.psbt` too); *Sign* shows what the PSBT does (inputs with amounts, outputs with the wallet's own change marked,
  the fee, an input amount that could not be verified) and signs with the wallet's keys through the same `PsbtSigner` as the
  CLI, so a PSBT that spends nothing of the wallet is refused with its reason. The result can be copied or saved as a
  `.psbt` file, and when the signatures are enough it can be broadcast, with the same check as Send (a reply naming another
  txid is settled by asking the server for the transaction). Editing the text discards the previous result, so a result is
  never saved or broadcast under another PSBT's text.
- **Multisig files:** the wallet information of a multisig wallet shows its descriptors with checksums and saves the
  configuration file (ADR 0061); the creation step loads such a file to fill the threshold, the type and the keys.
- **Creation step:** an optional seed passphrase (kept in the wallet, so the same key signs), an account number for
  *Show my key*, a QR of that key, and a retype-the-words confirmation for a seed generated in the wizard (not asked for a
  seed the user typed).
- The logic is in `MainWindowViewModel.Psbt.cs` and `.Multisig.cs`; only the pickers and the clipboard are in the view's
  code-behind, with bounded reads (a PSBT up to 4 MiB, a configuration up to 64 KiB).

## Consequences

- A 2-of-3 can be completed entirely in the GUI: one cosigner prepares, the others sign and pass it on, the last broadcasts.
- Not done: animated QR (BC-UR) transport of PSBTs; only the cosigner key is shown as a QR.
- Not verified against a real signer, Sparrow or Electrum (ADR 0061).

## References

- `src/App/ViewModels/MainWindowViewModel.Psbt.cs`, `MainWindowViewModel.Multisig.cs`, `MainWindowViewModel.WalletInfo.cs`, `src/App/Views/MainView.axaml(.cs)`
- `tests/PalladiumWallet.Tests/App/PsbtGuiTests.cs`, `MultisigWizardTests.cs`
- ADR 0056, ADR 0059, ADR 0061
