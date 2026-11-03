# 0015. Imported-key and watch-only accounts

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Users need to import WIF keys, track an xpub, or watch plain addresses without exposing key material.

## Decision

`IWalletAccount` abstracts HD and imported accounts. `ImportedKeyAccount` holds a fixed address list (no HD derivation; change returns to the first imported address). Watch-only accounts (from xpub, or from addresses with no keys) report no private key, and `TransactionFactory` only signs when the account has one, so they can only ever produce an unsigned PSBT.

## Consequences

- Watch-only is structurally read-only, not just disabled in the UI.
- Imported-key wallets reuse addresses for change, a privacy trade-off.
- Pure address imports derive the script kind only informationally.

## References

- `src/Core/Crypto/IWalletAccount.cs`
- `src/Core/Crypto/ImportedKeyAccount.cs`
- `src/Core/Wallet/WalletLoader.cs`
