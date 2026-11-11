# 0056. Add native SegWit multisig wallets from the CLI

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-10

## Context

ADR 0031 deferred multisig with the other first-release exclusions. The profile already defines the multisig
SLIP-132 headers (`Zpub`/`Vpub`) and BIP48, and the PSBT-centric signing flow (ADR 0045) is what a multisig wallet needs:
the signatures of several parties travel as a PSBT. Palladium Core can derive the reference addresses
(`deriveaddresses` of an output descriptor), so correctness can be checked against an independent implementation.
This ADR supersedes ADR 0031 for multisig only; Lightning, Tor, hardware wallets and the rest stay deferred.

## Decision

- **Script and keys.** Native SegWit P2WSH only. Each cosigner contributes the key at `change/index` under its BIP48
  account xpub (`m/48'/coin'/account'/2'`). The keys are sorted (BIP67) and the address is
  `wsh(sortedmulti(M, …))`, exactly as Bitcoin Core and Sparrow build it, so the entry order of the cosigners does not
  change the wallet. Wrapped (P2SH-P2WSH) multisig is not implemented and says so.
- **Keys exchanged as `[fingerprint/path]Zpub…`** (the key-origin form of descriptors, `Core/Crypto/Cosigner.cs`).
  The origin is mandatory and must agree with the key (depth, last step), because a PSBT signer finds its key by
  fingerprint. Standard `xpub`/`tpub` forms are accepted too. It is untrusted input: parsed without throwing, and a
  fuzz target (`cosignerkey`) holds that contract.
- **Wallet file.** `MultisigThreshold` and the N cosigner keys (public data). A seed (or account xprv), if present,
  is one cosigner's own key, found by fingerprint and checked against its xpub. Without it the file is a watch-only copy.
- **Refused at creation:** fewer than two or more than 15 cosigners, `M` outside `1..N`, the same key twice (one person
  would hold two signatures), a seed that matches no cosigner or more than one, an origin that contradicts its key.
- **Spending is always a PSBT.** The wallet builds the transaction, signs its own share into the PSBT, and attaches
  what the other signers need: the full previous transactions, every cosigner's key origin per input and for the
  change, the witness script of the change output, the cosigners' account xpubs. `IWalletAccount.IsMultiSignature`
  makes `TransactionFactory` return an incomplete transaction (`Signed = false`) even when the wallet holds a key.
  `PsbtSigner` fills a missing witness script for the wallet's own addresses, so a PSBT from another creator can be
  signed, and `PsbtSigner.Combine` merges the cosigners' copies of one transaction and finalises it when `M` signatures
  are present.
- **CLI:** `cosigner-key`, `create-multisig`, `combine-psbt`; `sign-psbt`, `send`, `sync`, `info` work on the new wallet.
  `info` prints the descriptor, which a Bitcoin-Core-derived node can import watch-only.
- **GUI.** It opens, syncs and shows a multisig wallet and its Send tab prepares the PSBT; it does not create one.
  The confirm commands of Send and Donate now refuse any transaction that is not fully signed, whatever the state of the
  button, so an incomplete transaction cannot be broadcast.

## Consequences

- A multisig wallet moves funds only with `M` signatures: no single device can, and up to `N - M` keys can be lost.
- The wallet does not authenticate the cosigners' keys. A key swapped in transit is accepted, and the user must compare
  the first address and the fingerprints on every device before receiving funds. The user guide and SECURITY.md say so.
- Verified against the node: the receive and change addresses of a 2-of-3, and the 1-of-3 and 3-of-3 variants, equal
  `deriveaddresses` of the descriptor; a 2-of-3 spend signed by two cosigners (one of them through `combine-psbt`) was
  broadcast and mined on regtest (`tests/regtest/e2e.sh`).
- Not covered: hardware signers, other wallets' PSBT dialects, P2SH-P2WSH, unequal key weights, creating the wallet in the GUI.

## References

- `src/Core/Crypto/MultisigAccount.cs`, `src/Core/Crypto/Cosigner.cs`, `src/Core/Wallet/WalletLoader.cs`
- `src/Core/Wallet/TransactionFactory.cs`, `src/Core/Wallet/PsbtSigner.cs`, `src/Cli/Program.cs`
- `tests/PalladiumWallet.Tests/Crypto/MultisigAccountTests.cs`, `tests/PalladiumWallet.Tests/Wallet/MultisigSpendTests.cs`
- `tests/regtest/e2e.sh`; ADR 0031, 0038, 0045, 0055; BIP48, BIP67, BIP174
