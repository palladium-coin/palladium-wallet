# 0014. BIP32/39 HD wallets with BIP44/49/84 paths and SLIP-132 keys

- **Status:** Accepted
- **Area:** Crypto
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Users expect seed compatibility with other wallets and a choice of address format.

## Decision

Derive accounts with BIP39 + BIP32 using purposes 44 (legacy P2PKH), 49 (wrapped P2SH-P2WPKH) and 84 (native P2WPKH), coin type 746 from the profile (`DerivationPaths`). Extended keys are encoded and parsed as SLIP-132 (xpub/ypub/zpub families) with headers from the profile (`Slip132`), since NBitcoin only registers xprv/xpub. An optional BIP39 passphrase is supported. Native SegWit is the default kind.

## Consequences

- Restore from seed or xpub/xprv reproduces the same addresses; golden vectors compare against a reference wallet.
- Taproot (BIP86) and multisig (BIP48) constants exist, but derivation for them is not implemented (see 0031).

## References

- `src/Core/Crypto/DerivationPaths.cs`
- `src/Core/Crypto/Slip132.cs`
- `src/Core/Crypto/HdAccount.cs`
- `src/Core/Crypto/Bip39.cs`
