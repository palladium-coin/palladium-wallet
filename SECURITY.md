# Security

## Threat model

Palladium Wallet is a self-custody SPV wallet. It is designed to protect funds against:

- Theft of the wallet file at rest (AES-256-GCM encryption with PBKDF2-HMAC-SHA512)
- Memory snooping of private keys after unlock (keys are held only in process memory, never written to disk in plaintext unless the user explicitly disables encryption)
- Fraudulent transaction injection by a malicious server (every confirmed transaction is verified with a Merkle proof against SPV-validated block headers anchored to hardcoded checkpoints)

It does **not** protect against:

- A fully compromised operating system or process (malware with memory access can extract keys from RAM)
- An attacker who obtains the wallet file **and** the password
- Denial of service or eclipse attacks against the indexing server
- Network-level traffic analysis (no Tor/proxy support in the first release)

---

## SPV trust model

This wallet is an SPV client, not a full node. It validates:

- Block headers: `SkipPowValidation` is enabled because LWMA difficulty cannot be recomputed client-side, so proof of work is never checked. Instead, every header used for a Merkle proof is hash-chain-linked (prev-hash) back to the nearest hardcoded checkpoint at or below its height, and that checkpoint's hash must match exactly (`Core/Chain/ChainProfiles.cs`, enforced in `Core/Spv/WalletSynchronizer.AnchorToCheckpointAsync`). Currently populated for mainnet only (every 20,000 blocks); testnet/regtest have no checkpoints yet, so headers there are trusted at face value
- Transaction inclusion in a confirmed block (Merkle branch proof, mandatory for every confirmed transaction — see `Core/Spv/MerkleProof.cs`)

It does **not** validate:

- Script execution (P2WPKH scripts are assumed valid if the server returns a confirmed transaction with a valid Merkle proof)
- Double-spend detection beyond what the server reports (an eclipse attack on the indexing server could hide a conflicting transaction)
- Full block validity (coinbase, consensus rules beyond the header)

The indexing server (ElectrumX-compatible, port 50001/50002) is a **semi-trusted** component. It can:

- Lie about unconfirmed (mempool) transactions — the wallet shows mempool transactions as unconfirmed and non-spendable
- Refuse to relay a broadcast transaction
- Delay reporting of new blocks

It cannot (given correct Merkle verification):

- Fabricate a confirmed transaction with a valid Merkle proof
- Forge a payment to a wrong address

**Network resource limits.** Server input is bounded so a hostile server cannot hang or kill the
client: every request has a timeout (30 s; 10 s for the handshake; a timeout drops the connection)
and a single reply line is capped at 16 MiB (`Core/Net/ElectrumClient.cs`, ADR 0034 and 0035).

**Progressive verification (mobile-friendly sync).** On a wallet with many historical
transactions, `WalletSynchronizer` no longer blocks the whole sync on every Merkle proof:
balance and history are shown as soon as transaction downloads finish (`PartialResult`,
`Core/Spv/WalletSynchronizer.cs`), while proofs continue to be checked in the background and
each transaction's `Verified` flag catches up progressively. This means the UI can display a
server-reported balance/history that includes not-yet-verified entries — clearly marked with a
"verifying…" badge and a separate non-spendable total (`PendingVerificationSats`). The
security-critical invariant this depends on: coin selection (`TransactionFactory`, gated by
`Wallet/UtxoSpendability.IsSpendable`) refuses to spend a UTXO whose `Verified` flag isn't true,
regardless of confirmations — so a server that fabricates a fake confirmed balance can get it
*displayed* early, but never *spent*, before the forged Merkle proof is caught and the sync
fails outright. The disk cache (`SyncCache`) only ever persists the fully-verified end state of
a sync, never a partial one, so no unverified data survives a restart.

---

## Key and seed management

- The BIP39 mnemonic and derived private keys exist only in process memory after unlock
- Private keys are never written to disk, logged, or sent over the network
- The wallet file stores the encrypted seed (with password) or the encrypted/plaintext `WalletDocument` — the document contains the account xpub and sync cache, not the raw seed when watch-only
- Watch-only wallets (`restore-xpub`, or `restore-address` for pure address imports) hold no private keys and cannot sign transactions; `TransactionFactory` only calls `AddKeys`/signs when the account reports a private key, so an address-only import can never produce a signed transaction, only an exportable unsigned PSBT

---

## Encryption at rest

- Algorithm: AES-256-GCM
- Key derivation: PBKDF2-HMAC-SHA512, 600 000 iterations, 16-byte random salt (fresh salt and nonce on every save; the iteration count is stored in the file container, so future increases remain backward-compatible — bounded at 10 000 000 on read, since the count is attacker-controlled in a tampered file and an absurd value would hang the wallet at open)
- Authentication: GCM tag (16 bytes) — any tampering is detected before decryption
- A malformed container (broken JSON, missing fields, bad base64, wrong nonce/tag size) always fails with a typed `InvalidDataException`, never a raw parser exception — the decrypt path is fuzz-tested (`tests/PalladiumWallet.Fuzz`)
- The user can explicitly opt out of encryption (UI shows a warning); the `WalletStore.Save` API accepts `null` password only when the caller has confirmed user intent

---

## TLS certificate pinning

Connections to the indexing server use TOFU (Trust On First Use): the server's TLS certificate is pinned on first connection and stored in `server-certs.json`. A certificate change triggers a hard error (`CertificatePinMismatchException`) requiring explicit reset by the user. This prevents silent MITM substitution after first connection.

---

## Backup

The wallet file is the only thing that needs to be backed up. For encrypted wallets, the password is also required. If both the file and the password are lost, funds are unrecoverable (no server-side backup). For watch-only wallets (restored from xpub or from a plain address), the private keys must be kept in a separate cold storage device.

---

## AI-assisted testing and vulnerability discovery

Part of the test suite and security review for this project is produced with **Claude Fable 5** (Anthropic), used as a targeted tool rather than a blanket "AI-reviewed" stamp. Concretely:

- **Adversarial network simulation**: the SPV/network layer (`ElectrumClient`, `WalletSynchronizer`, `CertificatePinStore`) is tested against an in-process fake ElectrumX server that can be programmed to lie — serve a transaction with a Merkle proof that doesn't match its claimed block header, drop connections mid-request, return malformed or throttling responses. These are exactly the behaviors a malicious or compromised indexing server would exhibit; the suite asserts the wallet detects and rejects them (see `SpvVerificationException`) instead of trusting unverified server data.
- **Property-based fuzzing** (CsCheck): parsers and cryptographic roundtrips (amount parsing, SLIP-132 key encoding, Merkle proof verification, AES-GCM encrypt/decrypt) are exercised against hundreds of generated inputs per run, checking invariants — no crash on arbitrary input, correct rejection of a wrong password, no false-negative Merkle verification — rather than a handful of hand-picked cases. This methodology has already found and fixed two real defects: a corrupted TLS pin file that permanently blocked reconnection, and a wallet-detection routine that threw on unexpected but valid JSON instead of failing safe.
- **Targeted security-focused code review** over the areas where a bug has direct financial impact: key derivation, transaction signing, coin selection/spendability rules, and encryption at rest — cross-checked against the invariants stated in this document (e.g. "the server cannot fabricate a confirmed transaction with a valid Merkle proof").

This is a complement to, not a substitute for, independent human or third-party security review — which is still recommended before relying on this wallet for significant mainnet funds, particularly ahead of a 1.0 release.

---

## Known limitations and out-of-scope for v1

- No Tor/proxy support (network traffic reveals which addresses are being queried)
- No multi-server pooling (single point of failure for the indexing server)
- No hardware wallet integration
- No coin control (automatic UTXO selection only)
- No RBF/CPFP UI (RBF flag is set on all transactions, but fee bumping is not exposed)
- No Lightning Network support
- No multisig (M-of-N) wallets: the network profile defines multisig SLIP-132 header
  variants, but derivation for them is not implemented — attempting to use one throws
  rather than silently producing an insecure/incorrect wallet
