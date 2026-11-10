# 0057. Make the cosigner key check a wallet ID the cosigners compare

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-10

## Context

ADR 0056 left one limit open: the wallet cannot authenticate a cosigner's key. There is no certificate or signature
that binds a public key to a person; a key swapped on its way (by whoever controls the channel the cosigners use to
exchange them) is indistinguishable from a genuine one, and the wallet would then include a stranger among the
cosigners. The only sound defence is for the cosigners to compare what each of them built over a channel the attacker
does not control. What the wallet can do is make that comparison short, complete and enforceable. Hardware multisig
signers (Coldcard, Ledger) do it the same way: they show a short fingerprint of the whole configuration to compare.

## Decision

- **Wallet ID.** `MultisigAccount.WalletId` is the BIP380 checksum (8 characters, `DescriptorChecksum`) of the receive
  descriptor `wsh(sortedmulti(M,…))`, the same value Palladium Core's `getdescriptorinfo` returns for it. The keys in the
  descriptor are put in a canonical order, so the ID does not depend on the order each cosigner typed them in (the
  addresses never did); it changes if any key, any origin or the threshold changes.
- **Where it shows.** `create-multisig` prints it, `info` prints it, the GUI wallet information shows it with the
  threshold. When no verification was asked for, `create-multisig` says `NOT VERIFIED`.
- **`create-multisig --expect-id ID`** compares the ID (case-insensitive) before anything is written. A mismatch is an error
  that names both IDs and writes no file. The first cosigner creates the wallet and passes the ID to the others by
  another channel; theirs are created with `--expect-id`.
- **A multisig wallet signs only what it recognises.** `PsbtSigner` refuses an input that does not spend one of the
  wallet's own addresses (or whose amount is unknown), and one whose witness script is not the wallet's own, since the
  PSBT's witness script is chosen by whoever made the PSBT.

## Consequences

- A key swapped in transit is detected as soon as two cosigners compare IDs, and with `--expect-id` it cannot be
  turned into a saved wallet by mistake. It is not prevented: a cosigner who skips the comparison is still exposed,
  and the user guide and SECURITY.md say so.
- The ID is only as good as the channel it is compared over; an attacker who controls both the key exchange and the
  comparison defeats it, as with any out-of-band verification.
- The descriptor text, and so the ID, would change if the descriptor format changed; it is derived, not stored.
- Not done: signing the cosigner keys (no identity to bind them to), a GUI creation flow where the ID is typed in.

## References

- `src/Core/Crypto/DescriptorChecksum.cs`, `src/Core/Crypto/MultisigAccount.cs` (`WalletId`, `ToDescriptor`)
- `src/Cli/Program.cs` (`CreateMultisig`), `src/Core/Wallet/PsbtSigner.cs`, `src/App/ViewModels/MainWindowViewModel.WalletInfo.cs`
- `tests/PalladiumWallet.Tests/Crypto/MultisigAccountTests.cs`, `DescriptorChecksumTests.cs`, `tests/regtest/e2e.sh`
- ADR 0056; BIP380
