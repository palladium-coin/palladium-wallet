# 0045. Clamp the chain tip with a witness, validate TLS by default, build PSBTs from the shown transaction and sign them

- **Status:** Accepted
- **Area:** SPV
- **Recorded:** 2026-11-09

## Context

Follow-up to ADRs 0043 and 0044, closing the items they left open.

1. **The chain tip is a single server's word.** Every confirmation count (the 6 confirmations required to spend, coinbase maturity) is `tip − height + 1`. A primary server that reports a tip higher than the real one makes a fresh, possibly fraudulent output look deeply confirmed and spendable. The tip header cannot prove its height without checking proof of work, which this chain's LWMA does not allow.
2. **A missing pin store meant "trust anyone".** `ElectrumClient.ConnectAsync` accepted any certificate whenever `pins` was `null`, ignoring `SslPolicyErrors`. Every shipped caller passes a pin store, but the safe default was one forgotten argument away.
3. **The PSBT could be a different transaction from the one shown.** `BuildTransaction` and `BuildPSBT` were two separate builder runs, and the builder shuffles output order: in about a third of the runs the PSBT of a watch-only wallet held another ordering than the transaction whose txid and fee had just been shown.
4. **An offline signer could not check what it signs.** The PSBT carried only `witness_utxo` amounts, which cannot be verified (a lying amount turns into fee), and the CLI had no command to sign a PSBT at all. The GUI import wizard did not ask for the key origin that ADR 0044 made useful.

## Decision

- **Tip clamp.** `WalletSynchronizer` asks a witness (ADR 0042) for its tip, in parallel with the address scan. When the primary is more than `TipSkewTolerance` (2) blocks ahead of it, the lower tip is used (`SyncResult.TipHeight` included) and the progress line says so. A witness that is merely behind only delays spending; a witness that cannot answer, or no witness, leaves the primary tip. Witness connections are shared with the header cross-check and opened once per synchronizer.
- **TLS.** With `pins == null` the ordinary chain and host-name validation applies. A diagnostic tool may opt out explicitly with `acceptAnyCertificate: true` (the server probe does). With a pin store, behaviour is unchanged: TOFU pinning.
- **One transaction.** The PSBT is built from the transaction returned to the caller (signatures stripped, then re-added with the keys when the wallet can sign). The full previous transaction is attached to every input, so a signer can check amounts, and legacy inputs, which require it, are valid.
- **Signing.** `PsbtSigner` and the CLI command `sign-psbt <base64|-> [--scan N] [--broadcast]` sign a PSBT with the wallet's keys, finalise it when complete and print a summary first: inputs with amounts (flagged when not backed by a previous transaction), outputs with the wallet's own marked as change, and the fee. It refuses a watch-only wallet, any sighash other than `SIGHASH_ALL` (`NONE`, `SINGLE` or `ANYONECANPAY` would let whoever holds the PSBT change the outputs), and a previous transaction that does not hash to its outpoint.
- **GUI.** The xpub/xprv import step takes the optional master fingerprint and account path, validated and stored as in the CLI.

## Consequences

- A lying primary can no longer inflate confirmations while a witness is reachable; against two colluding servers, or an attacker in front of both, nothing changes (as in ADR 0042). The cost is one more connection on every wallet, not only those with history above the newest checkpoint.
- Callers that relied on `pins: null` to reach a self-signed server now fail with an authentication error; there are none in the shipped code.
- The txid shown before signing is final only for native segwit. A legacy or P2SH-wrapped input carries a scriptSig, which is part of the txid, so the signed transaction has a different id.
- PSBTs are larger by the size of the transactions they spend.
- Not addressed: the txid does not cover witness data; a peer can still announce a public address it controls and the user can pick it (ADR 0044); first contact with a self-signed server is trusted on first use, so a man in the middle at that moment is not detected.

## References

- `src/Core/Spv/WalletSynchronizer.cs`
- `src/Core/Net/ElectrumClient.cs`
- `src/Core/Wallet/TransactionFactory.cs`
- `src/Core/Wallet/PsbtSigner.cs`
- `src/Cli/Program.cs`
- `src/App/ViewModels/MainWindowViewModel.Wizard.cs`
- `tests/PalladiumWallet.Tests/Wallet/PsbtSignerTests.cs`
- `tests/PalladiumWallet.Tests/App/WizardOriginTests.cs`
- Related: 0042, 0043, 0044
