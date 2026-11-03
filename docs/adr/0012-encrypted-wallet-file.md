# 0012. Wallet file encrypted with AES-256-GCM and PBKDF2-HMAC-SHA512

- **Status:** Accepted
- **Area:** Storage
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

The wallet file contains the seed and keys; theft of the file must not mean theft of funds.

## Decision

`EncryptedFile` wraps the whole wallet document in a self-describing JSON container (`plm-wallet-aesgcm-v1`: kdf, iterations, salt, nonce, ciphertext, tag). AES-256-GCM, key from PBKDF2-HMAC-SHA512 with 600,000 iterations, a fresh 16-byte salt and 12-byte nonce on every save. The iteration count is stored in the file, so it can be raised later, and is capped at 10,000,000 on read because it is attacker-controlled. A wrong password or tampering raises `WrongPasswordException`; any malformed container raises `InvalidDataException`, never a raw parser error. Writes are atomic (temp file + rename).

## Consequences

- Tampering is detected before decryption; parameter upgrades stay backward compatible.
- The GCM tag cannot tell a wrong password from a corrupted file.
- Users may opt out of encryption (`Save` with a null password), only with an explicit UI warning.
- Changes to parameters or format must update `SECURITY.md` in the same commit.

## References

- `src/Core/Storage/EncryptedFile.cs`
- `src/Core/Storage/WalletStore.cs`
- `SECURITY.md`
