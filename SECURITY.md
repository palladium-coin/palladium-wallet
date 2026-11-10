# Security

## Threat model

Palladium Wallet is a self-custody SPV wallet. It is designed to protect funds against:

- Theft of the wallet file at rest (AES-256-GCM encryption with PBKDF2-HMAC-SHA512)
- Memory snooping of private keys after unlock (keys are held only in process memory, never written to disk in plaintext unless the user explicitly disables encryption)
- Fraudulent transaction injection by a malicious server (every confirmed transaction is verified with a Merkle proof against a block header that hash-chains up to a hardcoded checkpoint; the blocks above the newest checkpoint are the known exception, see the SPV trust model)

It does **not** protect against:

- A fully compromised operating system or process (malware with memory access can extract keys from RAM)
- An attacker who obtains the wallet file **and** the password
- Denial of service or eclipse attacks against the indexing server
- Network-level traffic analysis (no Tor/proxy support in the first release)

---

## SPV trust model

This wallet is an SPV client, not a full node. It validates:

- Block headers: `SkipPowValidation` is enabled because LWMA difficulty cannot be recomputed client-side, so proof of work is never checked. Instead, every header used for a Merkle proof is hash-chain-linked (prev-hash) **up to the nearest hardcoded checkpoint at or above its height**, and the header at that checkpoint height must hash to the checkpoint exactly (`Core/Chain/ChainProfiles.cs`, enforced in `Core/Spv/WalletSynchronizer.AnchorToCheckpointAsync`, ADR 0040). The direction matters: with PoW skipped, a chain built *from* a checkpoint *up to* a header proves nothing, because anyone can fabricate such a chain; a chain ending in a known hash cannot be forged without inverting SHA-256. Mainnet checkpoints run every 20,000 blocks from 20,000 (so even the oldest blocks are covered) and are extended at each release. **Known limit:** a transaction in a block *above the highest checkpoint* has no checkpoint to anchor to, so only its Merkle proof against the header the server supplied is checked there; the window is the blocks mined since the last release. The wallet flags such transactions as **not anchored** in the history list and in the transaction detail (ADR 0041), so the reduced guarantee is visible instead of silent. For those blocks the wallet also cross-checks the header with a second server taken from the hardcoded bootstrap list (never from announced peers): a different header at a safe depth (3+ blocks) aborts the sync, a shallower disagreement leaves the proof unverified until the next sync, and with no reachable witness only the Merkle proof applies (ADR 0042). This raises the bar from one lying server to two; it does not help against two colluding servers or an attacker in front of both. Only headers proven to chain to a checkpoint are written to the wallet cache or read back from it; headers above the newest checkpoint are re-fetched at every sync and proofs for blocks less than 24 deep are re-verified, so a reorganisation cannot leave a stale header or verdict behind, and a header-range reply is rejected unless its count and length are exactly what was requested (ADR 0043; the check is a fuzzed parser, `Core/Spv/HeaderRange.cs`, ADR 0054). The chain tip, from which every confirmation count is derived, is also checked against the witness: if the primary reports a tip more than 2 blocks above the witness's, the lower one is used, so a primary cannot make fresh outputs look deeply confirmed (ADR 0045). Testnet/regtest have no checkpoints yet, so headers there are trusted at face value
- Transaction inclusion in a confirmed block (Merkle branch proof, mandatory for every confirmed transaction — see `Core/Spv/MerkleProof.cs`)

It does **not** validate:

- Script execution (P2WPKH scripts are assumed valid if the server returns a confirmed transaction with a valid Merkle proof)
- Double-spend detection beyond what the server reports (an eclipse attack on the indexing server could hide a conflicting transaction)
- Full block validity (coinbase, consensus rules beyond the header)

The indexing server (ElectrumX-compatible, port 50001/50002) is a **semi-trusted** component. It can:

- Lie about unconfirmed (mempool) transactions — the wallet shows mempool transactions as unconfirmed and non-spendable
- Refuse to relay a broadcast transaction
- Answer a broadcast with a txid other than the one of the transaction sent. The wallet compares the reply with the txid it computed itself (`BroadcastVerifiedAsync`); on a mismatch it does not believe either side, but asks the server for the transaction by its txid. A transaction the server returns with the right hash is reported as in the network, one it does not know stays pending and can be sent again (same signed bytes, harmless to repeat), and a check that cannot be made also keeps it pending (ADR 0047). The answer to that check comes from the same server, so a send is only certain once a sync shows it in the wallet history.
- Delay reporting of new blocks
- Suggest misleading fee estimates or relay fee (the wallet ignores implausible values, never goes below the relay fee, and always shows the absolute fee, flagging one above 1% of the amount — ADR 0039)

It cannot (given correct Merkle verification):

- Fabricate a confirmed transaction with a valid Merkle proof
- Serve a different transaction than the one requested for a txid: every downloaded (and every cached) raw transaction must hash to the txid it is filed under (`Core/Spv/VerifiedTransactions.cs`, ADR 0043), otherwise the Merkle proof, which covers only the txid, would not bind the content. The txid excludes witness data, so signatures of a served transaction are not covered (balances and amounts are)
- Forge a payment to a wrong address

**Chain identity.** Right after the handshake the wallet checks that the server is on the Palladium chain (`Core/Spv/ChainVerifier.cs`, ADR 0036): `genesis_hash` from `server.features` must match the profile, and the header at the highest checkpoint not above the server's tip must hash to the hardcoded checkpoint. Mainnet shares Bitcoin's genesis, so the checkpoint header is what actually distinguishes it; testnet/regtest (no checkpoints) rely on the genesis alone. A server whose tip is below the first checkpoint is accepted on the genesis check alone.

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
- The address list and the address detail sheet hold no private key: the WIF of an address is derived only when the user reveals it (after the wallet password, if the wallet is encrypted) and is dropped when it is hidden, when the sheet closes or when the wallet closes (ADR 0037). The key is a managed string and cannot be zeroed, so it remains in process memory until the garbage collector reclaims it
- The wallet file stores the encrypted seed (with password) or the encrypted/plaintext `WalletDocument` — the document contains the account xpub and sync cache, not the raw seed when watch-only
- Watch-only wallets (`restore-xpub`, or `restore-address` for pure address imports) hold no private keys and cannot sign transactions; `TransactionFactory` only calls `AddKeys`/signs when the account reports a private key, so an address-only import can never produce a signed transaction, only an exportable unsigned PSBT

---

## Encryption at rest

- Algorithm: AES-256-GCM
- Key derivation: PBKDF2-HMAC-SHA512, 600 000 iterations, 16-byte random salt (fresh salt and nonce on every save; the iteration count is stored in the file container, so future increases remain backward-compatible — bounded at 10 000 000 on read, since the count is attacker-controlled in a tampered file and an absurd value would hang the wallet at open)
- Authentication: GCM tag (16 bytes) — any tampering is detected before decryption
- A malformed container (broken JSON, missing fields, bad base64, wrong nonce/tag size) always fails with a typed `InvalidDataException`, never a raw parser exception — the decrypt path is fuzz-tested (`tests/PalladiumWallet.Fuzz`)
- The user can explicitly opt out of encryption (UI shows a warning); the `WalletStore.Save` API accepts `null` password only when the caller has confirmed user intent
- The wallet file is written owner-only (0600 on Unix, whether or not it is encrypted), flushed to disk and renamed over the old one, so a crash leaves the old file or the new one, never a partial one. The single-instance lock file is removed in the same step that releases the lock (ADR 0044)

---

## TLS certificate pinning

Connections to the indexing server use TOFU (Trust On First Use): the server's TLS certificate is pinned on first connection and stored in `server-certs.json`. A certificate change triggers a hard error (`CertificatePinMismatchException`) requiring explicit reset by the user. This prevents silent MITM substitution after first connection. **Known limit:** the first contact with a self-signed server is trusted as it is, so a man in the middle at that very moment is not detected. A connection made without a pin store is validated like any HTTPS connection (chain and host name); nothing in the wallet disables that, and only the server probe tool opts out explicitly (ADR 0045).

---

## PSBT signing

`sign-psbt` signs a PSBT that came from outside, so it is treated as untrusted input (ADR 0045): only `SIGHASH_ALL` is signed, a previous transaction attached to an input must hash to the outpoint it is supposed to fund, a watch-only wallet refuses, and the inputs, outputs (with the wallet's own change marked) and fee are printed before the result. An input amount that is not backed by a full previous transaction is flagged as unverified. The PSBTs the wallet itself produces are built from the very transaction whose txid and fee were shown, and carry the previous transactions and, when the master fingerprint is known, the BIP32 origin.

## Servers announced by peers

Servers learned through peer discovery are listed and can be selected by the user, but the wallet never connects to one on its own: the automatic fallback after a connection failure uses only the servers shipped in the profile's bootstrap list, and peers naming `localhost`, private, link-local or otherwise non-public addresses are dropped (ADR 0044). A hostile server therefore cannot steer the wallet to servers of its choosing or have it probe the user's own network.

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
- Coin control is a manual selection of coins and a freeze flag (ADR 0046), with no privacy-aware selection
- No RBF/CPFP UI (RBF flag is set on all transactions, but fee bumping is not exposed)
- No Lightning Network support
- Multisig (M-of-N) is native SegWit (P2WSH) or P2SH-wrapped (P2SH-P2WSH), created from the CLI or the GUI, with no
  hardware signer; only Palladium Core has been exercised as the external party for PSBTs. A cosigner
  key cannot be authenticated by the wallet (there is no PKI; the key is just a public key): a key swapped while it
  travels between the cosigners would put a stranger among them. What the wallet does is make the check cheap and
  enforceable: the **wallet ID** (the BIP380 checksum of the descriptor, covering every key and the threshold,
  independent of entry order) is printed at creation, shown in `info` and in the GUI (in the creation flow, where a
  mismatching ID typed in writes nothing, and in the wallet information), and `create-multisig --expect-id` refuses to write a wallet whose ID differs from the one another cosigner saw. The
  comparison itself must happen over a channel the attacker does not control; without `--expect-id` the wallet says
  `NOT VERIFIED`. A multisig wallet also signs only inputs it recognises as its own addresses and refuses a PSBT whose
  witness (or redeem) script is not its own (ADRs 0057, 0058, 0059)
