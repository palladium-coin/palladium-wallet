# 0058. Add P2SH-wrapped multisig

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-10

## Context

ADR 0056 made native SegWit (P2WSH) the only multisig type. Some signers and services cannot pay to or read a bech32
address and expect the older P2SH-P2WSH form (BIP48 script type `1'`, descriptor `sh(wsh(sortedmulti(…)))`). The
profile already carried the `Ypub`/`Upub` headers for it, but the wallet threw rather than produce such a wallet.

## Decision

- `MultisigAccount` takes a `ScriptKind`: `NativeSegwitMultisig` (default) or `WrappedSegwitMultisig`. A wrapped
  address is the P2SH of the witness program (`GetRedeemScript`, the P2WSH script of the witness script); the descriptor
  is wrapped in `sh(…)`, so the wallet ID (its BIP380 checksum) differs from the native wallet of the same keys.
- The kind is stored in the wallet document's `ScriptKind` and read back by `WalletLoader`; cosigner keys are written
  with the `Upub`/`Ypub` header and the `…/1'` path.
- The PSBT of a wrapped wallet carries both scripts on every input and on the change output; `PsbtSigner` fills in the
  redeem script as it does the witness script, and refuses an input that carries a redeem script that is not the
  wallet's own. A wrapped spend's txid is not final until signed (the scriptSig joins on finalisation), unlike native.
- CLI: `--wrapped` on `cosigner-key` and `create-multisig`; `info` names the type.

## Consequences

- The addresses and the wallet ID were checked against Palladium Core v2.1.0 (`deriveaddresses`, `getdescriptorinfo`),
  and a 2-of-3 wrapped spend was signed by two cosigners, broadcast and mined on regtest (`tests/regtest/e2e.sh`).
- A wrapped input is bigger than a native one (the redeem-script push), so the same spend costs a little more fee.
- Not done: hardware signers, and a check against Sparrow or Electrum themselves: only Palladium Core as the external
  party has been exercised.

## References

- `src/Core/Crypto/MultisigAccount.cs`, `src/Core/Wallet/PsbtSigner.cs`, `src/Core/Wallet/TransactionFactory.cs`, `src/Core/Wallet/WalletLoader.cs`
- `tests/PalladiumWallet.Tests/Wallet/WrappedMultisigTests.cs`, `tests/regtest/e2e.sh`
- ADR 0056, ADR 0057; BIP48
