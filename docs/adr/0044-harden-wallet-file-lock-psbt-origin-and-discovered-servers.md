# 0044. Harden the wallet file and lock, carry BIP32 origin in the PSBT, distrust announced servers

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-08

## Context

The same audit that led to ADR 0043 found four more gaps.

1. **Wallet file.** `WalletStore.Save` wrote a temp file with default permissions (0644 on Linux, so a wallet saved without a password exposed its mnemonic and WIF keys to other local users), did not flush to disk before the rename, and shared one `.tmp` name between `Save` and `SaveAsync`, so two overlapping saves could collide.
2. **Wallet lock.** `WalletLock.Dispose` released the lock and then deleted the lock file. In between, another process could lock the old file while a third created and locked a new one at the same path: two owners of one wallet.
3. **PSBT.** A watch-only wallet exported a PSBT with only `witness_utxo`: no `bip32_derivs`, no account xpub. An external or hardware signer could not match its keys nor recognise the change output. Importing an xpub also recorded a default account path and no fingerprint, so any origin would have been invented.
4. **Discovered servers.** Servers announced by a peer were added to the known list and tried automatically as fallbacks when the chosen server failed, and could become the CLI default on profiles with no bootstrap list. A hostile server could steer the wallet to its own servers, or to `localhost`/private addresses.

## Decision

- **Save.** One save at a time per process; the temp file is created owner-only (`UnixCreateMode` 0600; a stale temp file is removed first), written, flushed to disk, then renamed over the wallet. The saved wallet is therefore 0600 whether or not it is encrypted.
- **Lock.** The lock file is opened with `FileOptions.DeleteOnClose`, so it disappears in the same step that releases the lock. No delete after release.
- **PSBT origin.** When the master fingerprint is really known (seed wallet, or supplied at import), `TransactionFactory` adds to the PSBT the BIP32 origin of every key involved (inputs and the change output) and the account xpub with its origin. When it is not known, nothing is added: a made-up origin would mislead a signer. `restore-xpub` takes `--fingerprint HEX8` and `--path m/...`; the fingerprint is stored in the wallet file (`MasterFingerprint`) and restored on load. The path defaults to the standard one for the script type if `--path` is omitted, so it should be given together with the fingerprint.
- **Servers.** `ServerRegistry.Trusted` (and `Default`) are the profile's bootstrap servers only. The automatic fallback after a connection failure walks `Trusted`, never discovered servers; those stay listed and selectable by the user. Peer discovery skips hosts that cannot be public servers (empty or oversized, `localhost`, `.local`/`.internal`/`.lan` names, loopback, private, link-local, CGNAT, unspecified and multicast addresses, unique-local IPv6, malformed names) and replaces a port outside 1–65535 with the profile default.

## Consequences

- A plaintext wallet is no longer world-readable on shared Unix systems; a crash or power loss leaves the old wallet or the new one, never an empty file.
- Wallet files created by earlier versions have their permissions tightened at the next save. Windows has no permission bits; the file inherits the folder's ACL.
- A watch-only wallet imported from a bare xpub still exports a PSBT without origin until the fingerprint is supplied; the GUI import wizard does not ask for it yet.
- Discovered servers are no longer used unprompted. On testnet/regtest, which have no bootstrap list, the CLI needs `--server`, as before for an empty registry. A user who really runs a server on a private address types it in by hand.
- Not addressed: a peer can still announce a public address it controls; the user can pick it, and chain identity and Merkle checks still apply (ADRs 0036, 0040).

## References

- `src/Core/Storage/WalletStore.cs`
- `src/Core/Storage/WalletLock.cs`
- `src/Core/Wallet/TransactionFactory.cs`
- `src/Core/Crypto/HdAccount.cs`
- `src/Core/Net/ServerRegistry.cs`
- `src/App/ViewModels/MainWindowViewModel.Sync.cs`
- `tests/PalladiumWallet.Tests/Storage/WalletStoreHardeningTests.cs`
- `tests/PalladiumWallet.Tests/Wallet/PsbtOriginTests.cs`
- `tests/PalladiumWallet.Tests/Net/ServerTrustTests.cs`
- Related: 0043, 0036
