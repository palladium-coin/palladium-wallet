# 0053. Import WIF keys and create taproot wallets from the CLI

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-10

## Context

The GUI wizard imports WIF keys and `WalletLoader` builds taproot (BIP86) accounts, but the CLI offered neither:
`--kind` knew only segwit, wrapped and legacy, and any other value silently produced a native segwit wallet,
so a typo created a wallet of the wrong type. Taproot and WIF had never been exercised against a real node.

## Decision

- `restore-wif <keys|->` calls `WalletLoader.NewFromWif` and saves a spendable imported-key wallet. The keys come
  from the argument or, with `-`, from standard input, so they need not appear in the process list or the shell
  history. The wallet is encrypted like any other (a password, or `--no-encryption` on purpose).
- `--kind` accepts `segwit`, `wrapped`, `legacy` and `taproot`, and rejects anything else. It applies to
  `create`, `restore`, `addresses` and `restore-wif`.
- `info` shows "imported: N private key(s)" for a wallet without a derivation path, instead of an empty path and key.
- Taproot and WIF are verified by `tests/regtest/e2e.sh`: fund, sync, spend and broadcast against a node that
  enforces relay policy, then confirmed by mining.

## Consequences

- The CLI reaches the same wallet types as the GUI, except multisig, which is not implemented.
- A WIF wallet is a fixed set of keys: no derivation, no gap limit, no new addresses; the user must give the
  `--kind` that matches the funds. Said in the user guide.
- Taproot is key-path only (BIP86), as in the rest of the wallet.
- A wrong `--kind` is an error where it used to be accepted: scripts that relied on the silent default must name
  a valid kind.

## References

- `src/Cli/Program.cs` (`Kind`, `RestoreWif`, `Info`), `src/Core/Wallet/WalletLoader.cs`
- `tests/PalladiumWallet.Tests/App/CliTests.cs`, `tests/regtest/e2e.sh`
- ADR 0038 (CLI passwords), ADR 0051
