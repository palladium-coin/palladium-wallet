# Changelog

Technical changelog for PalladiumWallet. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/); entries are grouped
by subsystem rather than strictly by date, since `0.9.0` is the first
release and covers the full history from the initial commit.

## [1.0.0] — 2026-07-09

First stable release. Closes the last open security gap from 0.9.x (header
trust was not actually anchored to any checkpoint), fixes several crash
paths found by a new fuzzing harness, and adds OP_RETURN/coinbase-tag
decoding to the transaction detail view.

### Security

- `WalletSynchronizer.AnchorToCheckpointAsync`: header trust is now actually
  anchored to `ChainProfiles.Mainnet.Checkpoints` (24 real `[height, hash,
  bits]` checkpoints pulled from a fully-synced node, every 20,000 blocks
  plus one near tip). Previously the checkpoint array was empty and the
  methods meant to enforce it (`MatchesCheckpoint`/`IsValidChild`) were
  never called — on this LWMA chain, where PoW can't be recomputed
  client-side, a malicious or eclipsing server could hand back any
  internally-consistent header for a Merkle proof with nothing tying it to
  the real chain. For every header used in a Merkle proof, the intervening
  headers are now downloaded back to the nearest checkpoint and verified as
  an unbroken prev-hash chain terminating in that checkpoint's exact hash
  (memoized per sync session). Testnet/Regtest remain unanchored (no node
  available to source checkpoints from); a missing checkpoint is a no-op,
  not a failure.
- New fuzzing harness (`tests/PalladiumWallet.Fuzz`, SharpFuzz-based): one
  target per untrusted-input parser (header, Merkle proof, peer list,
  wallet file, mnemonic/key/address/amount), each encoding that parser's
  documented error contract. Found and fixed:
  - `Bip39.TryParse` threw `NotSupportedException` on text resembling no
    wordlist instead of failing gracefully.
  - `ElectrumApi.ParsePeers` threw on any `server.peers.subscribe` response
    shape other than the expected `[ip, hostname, [features...]]`, and on a
    JSON string containing invalid UTF-8.
  - `EncryptedFile.Decrypt` let a tampered/corrupted wallet file escape as
    raw `JsonException`/`FormatException`/`ArgumentNullException` instead of
    the documented `WrongPasswordException`/`InvalidDataException`
    contract; worse, the PBKDF2 iteration count was read from the
    (attacker-controlled) container with no upper bound — a tampered file
    demanding e.g. 2³¹ iterations would hang the wallet on open. Iteration
    count is now clamped to 10,000,000.
  - `CertificatePinStore.Load`: a corrupted pin file blocked every SSL
    connection until manually deleted; now falls back to first-contact
    TOFU like `ServerRegistry` already did.
  - The seed corpus (incl. a regression input per fixed finding) replays
    inside `dotnet test` via `FuzzCorpusTests`, so a fix can't silently
    regress.
- `SECURITY.md` corrected: PBKDF2 parameters (600,000 iterations / 16-byte
  salt, not the pre-hardening 100,000 / 32-byte), a stale file reference,
  and disclosure of the AI-assisted testing methodology used (adversarial
  fake-server simulation, property-based fuzzing, targeted security
  review) as a complement to, not a replacement for, independent review.

### Added

- Transaction detail view now decodes OP_RETURN output payloads (UTF-8, or
  hex if the bytes aren't valid text — multiple OP_RETURN outputs in one tx
  are each decoded independently) and coinbase scriptSig pool tags (e.g.
  `/slush/`, extracted as printable-ASCII runs amid the binary BIP34
  height/extranonce). Both were previously dropped entirely — discarded
  once no destination address could be derived from the script.
- Help overlay: "User guide" button next to "Report a bug", linking to
  `USERGUIDE.md` on GitHub.
- `USERGUIDE.md`: full end-user reference for GUI and CLI (wizard flows,
  script types, fees, gap limit, TOFU cert pinning, CLI commands) with the
  exact numbers the software enforces.

### Fixed

- Send/Donate/Sync/Wizard view models wrote status/error strings directly
  in Italian regardless of the active language; routed through `Loc` with
  the missing keys added. `CertificatePinMismatchException` no longer
  bakes an Italian message into `.Message` — it exposes `Host`/`Port` for
  the UI to translate.
- CLI (`src/Cli/Program.cs`) printed all output in Italian regardless of
  the code/docs-are-English-only policy; translated every user-facing
  string and comment.

### Testing

- Test suite expanded from 307 to 392 tests, closing coverage gaps in:
  checkpoint-anchoring (including the memoization and non-generic-retry
  branches), the PoW-checked branch of `BlockHeaderInfo.IsValidChild`
  (never run since every profile sets `SkipPowValidation`), all 8 BIP-39
  wordlist languages plus the empty-input guard, SLIP-132 rejection of
  malformed/corrupted keys, `TransactionFactory`'s standardness-policy
  rejection, `ImportedKeyAccount`'s fund-safety fallbacks,
  `WalletLoader`'s defensive branches for corrupted files,
  `PalladiumNetworks.For`/`INetworkSet`, `AppPaths`' full data-root
  precedence chain (via new internal override seams), `UpdateChecker`
  end-to-end via a stub-transport seam, and `ElectrumClient`'s
  multi-segment response dispatch.
- OP_RETURN/coinbase-tag decoding covered: UTF-8 text, binary fallback to
  hex, multiple OP_RETURN outputs in one tx, pool-tag extraction, and the
  absence of false positives on standard outputs/inputs.

### Documentation

- `AGENTS.md`/`CLAUDE.md` re-synced (had drifted) and reformatted from
  dense prose to scannable bullet lists; a stale `SECURITY.md` file
  reference fixed.
- `README.md` test-coverage section and `SECURITY.md` updated to describe
  the checkpoint anchoring and fuzzing guarantees actually enforced now.

## [0.9.1] — 2026-07-02

### Testing

- Test suite expanded from 239 to 307 tests; `Core` line coverage raised
  from ~50% to ~92% (branch coverage from ~41% to ~79%).
- In-process fake ElectrumX server (`tests/.../Net/FakeElectrumServer.cs`):
  a real loopback TCP socket speaking newline-delimited JSON-RPC 2.0,
  optionally TLS with a self-signed certificate, with per-method handlers
  and call counters — exercises the network stack against real socket code
  instead of mocks.
- New end-to-end coverage for previously untested network/SPV code:
  `ElectrumClient` (request pipelining, error mapping, notifications,
  disconnection, cancellation, TLS/TOFU handshake), `WalletSynchronizer`
  (gap-limit scanning, UTXO/history reconstruction, unconfirmed/immature
  balances, busy-retry, disk-cache reuse, and the security-critical path
  where a lying server fails Merkle verification and the sync aborts),
  `TransactionInspector` (fee calculation, mine/theirs attribution, RBF,
  coinbase handling), `CertificatePinStore` (TOFU pin/match/mismatch/reset),
  `ServerRegistry` peer discovery, and `UpdateChecker` tag parsing.
- `TransactionFactory`: added coverage for legacy/P2SH/segwit destinations,
  multi-UTXO selection, dust change absorbed into the fee, and a golden
  txid anchoring the PSBT signing path (deterministic via RFC 6979).
- Property-based tests extended: SLIP-132 roundtrip for every script kind
  and network, `WalletDocument` JSON roundtrip with arbitrary
  labels/contacts, `Scripthash` cross-checked against an independent
  SHA-256 computation.
- `update-version.sh`: single script to bump the version across the project
  ahead of a release tag.

### Fixed

- `CertificatePinStore.Load`: a corrupted pin file threw and blocked every
  SSL connection until the user manually deleted it; now falls back to
  first-contact TOFU, same as `ServerRegistry` already did for its own file.
- `EncryptedFile.IsEncrypted`: threw on valid JSON with a non-object root
  (e.g. a bare number or array) or on invalid UTF-16 input, instead of
  returning `false`. Both bugs were found by the expanded property tests.

### Documentation

- `README.md`: "Running tests" section rewritten with a per-area coverage
  table, the coverage-measurement command, and a description of the fake
  ElectrumX server.
- `CLAUDE.md`/`AGENTS.md` kept in sync, pointing future work at extending
  the fake ElectrumX server instead of mocking `ElectrumClient` (not an
  interface, by design).

## [0.9.0] — 2026-07-02

First release. SPV wallet (Sparrow-style) for the Palladium (PLM) network —
Bitcoin-derived UTXO chain — targeting desktop (Windows/Linux) and Android
from a single Avalonia UI codebase on .NET 10 + NBitcoin.

### Core — Chain

- Network profiles and consensus constants centralized in `Core/Chain`
  (`ChainProfiles`/`PalladiumNetworks`): address prefixes, BIP32 headers,
  bech32 HRP, genesis, ports, `coin_type` 746 — selectable per network
  (mainnet/testnet/regtest), no scattered magic numbers.
- LWMA difficulty / 2-minute blocks: SPV cannot recompute LWMA retargeting,
  so PoW validation is skipped and trust is anchored to hardcoded
  checkpoints instead.

### Core — Crypto

- HD key derivation: BIP32/39/84, HD accounts, SLIP-132 extended key
  serialization.
- Address types: P2PKH, P2SH-P2WPKH, and P2TR (Taproot/BIP86).
- `IWalletAccount` abstraction with WIF/xpub/xprv keystore import (including
  watch-only accounts from public material only).

### Core — Net

- Custom `ElectrumClient`: JSON-RPC 2.0 client for the ElectrumX-like
  indexing server, with TLS support and TOFU certificate pinning.
- `ServerRegistry`: bootstrap server list, peer discovery, persisted last-used
  server, fallback resolution for `--server`.
- Batched writes, zero-allocation reads, bounded in-flight requests for
  network throughput.
- Fix: allow changing the indexing server at any time, including during an
  active sync.

### Core — Spv

- Header sync with Merkle proof verification and scripthash subscriptions
  against hardcoded checkpoints (no full PoW recomputation, per the chain
  profile above).
- Per-address balance and transaction-count aggregation in sync results.
- Electrum-style continuous updates: incremental, parallelized sync across
  address chains, with a persistent header cache.
- Fix: resilient sync — history discovery via `GetHistory`, transaction
  caching, automatic reconnect fallback.

### Core — Storage

- Versioned, encrypted JSON wallet file (`WalletDocument`) with dedicated
  persistence and loader layer.
- `WalletLock`: prevents concurrent access to the same wallet file; acquired
  before load and before close (fixed a race where it wasn't).
- XDG-compliant data paths (`AppPaths`) with per-platform override seam used
  by the Android head; English as default UI language.
- Contacts list (name + address) persisted in `WalletDocument`.
- Documented caveat: `WalletStore.Save` writes plaintext JSON when the
  wallet is unencrypted — seed/keys are only ever encrypted-at-rest when the
  user opts into a password.

### Core — Wallet

- Coin selection, PSBT-centric transaction factory, and wallet loader.
- Confirmation-threshold enforcement before spending UTXOs; immature
  (coinbase) balance surfaced separately from confirmed balance; pending
  mempool balance shown, with unconfirmed UTXOs excluded from spending by
  default.
- Fix: reject amounts with sub-satoshi precision.

### App — Avalonia UI (shared, `src/App`)

- Single shared `MainView` (UserControl) hosted by `MainWindow` on desktop
  and as the single-view root on Android; single `MainWindowViewModel`
  (CommunityToolkit.Mvvm), later split into partial files by area
  (Wizard/Settings/Sync/Send/Contacts/Receive/...).
- Step-by-step setup wizard (replacing a single-form panel), including a
  first-run data-location step, multi-wallet chooser, confirm-password and
  encrypt toggle, and dedicated flows for creating a wallet vs. importing
  from xpub/xprv/WIF.
- In-app overlay pattern for details/settings/help (`IsXxxOpen` flags, no OS
  windows) replacing earlier nested submenus and a separate
  `AddressInfoWindow`: server settings, app settings (language/unit),
  wallet info (xpub, password-gated seed reveal), address detail
  (password-gated private key reveal), transaction detail with full
  on-chain data (inputs/outputs, no truncation), and a Help overlay
  (Info/Donate tabs).
- Connection-status indicator in the bottom bar; connect-before-wallet-open
  flow; persisted last-used server; server discovery split into its own
  always-usable button; mainnet hardcoded (network selector removed for the
  first release).
- Localization: `Loc` key → 6-language dictionary (it/en/es/fr/pt/de) with
  live language switching.
- Receive: QR code generation and copy-to-clipboard for the receive address.
- Android: QR code scanner for the Send address field.
- Centralized design system (color tokens, gradient hero, SVG tab icons);
  responsive layout for portrait mobile, unified tab bar (desktop +
  mobile), two-column desktop layout for Send/Receive; various mobile-only
  fixes (tab bar sizing/indicator overlap, text overflow, full-screen server
  overlay on mobile).
- App version shown in window title and Help overlay, read from the single
  `<Version>` source in the App csproj.
- In-app update check: compares the running version against the latest
  GitHub release tag on startup and shows an overlay with the new tag when
  one is available (best-effort — silent on network/parse failure).
- In-app bug-report button (Help overlay) opening a pre-filled GitHub issue
  template; issue/PR templates completed.

### Android head (`src/App.Android`)

- Architecture split: shared UI library (`src/App`) + `src/App.Desktop` +
  `src/App.Android` heads from the same source (`refactor(arch)`), each
  carrying only the per-platform entry point and packages.
- App logo as launcher icon.
- Persistent release-signing keystore workflow
  (`docker/keystore/generate-keystore.sh`, git-ignored output): every
  release APK is signed with the same key so installing a newer build
  updates a previous install in place instead of requiring an uninstall.
  `versionCode` derived from `<Version>` instead of a fixed constant.

### CLI (`src/Cli`)

- Commands: `create`/`restore`/`restore-xpub`/`info`,
  `sync`/`send`/`servers`/`reset-certs`, `newseed`/`addresses`.
- `servers` command, `info --addresses`, and registry-based fallback
  resolution for `--server`.

### Build & Distribution

- Docker-based reproducible build system (`docker/build.sh` +
  `docker/Dockerfile.*`): pinned toolchain (.NET 10 SDK, JDK, Android SDK),
  builds Windows/Linux single-file executables and a signed Android APK
  without any SDK installed on the host.
- Android release signing wired into `build_android` (see Android head
  above): requires the persistent keystore, prompts for its passwords at
  build time, mounts it read-only into the build container.

### Testing

- Unit test coverage across all Core modules, later expanded to 209 tests.
- Property-based tests via CsCheck (`PropertyTests.cs`), bringing total
  coverage to 218 tests; dedicated `WalletLock` concurrency tests.

### Documentation

- `README.md` (project overview, quickstart, reproducible builds), `CLAUDE.md`
  (codebase guidance for AI tooling, kept in sync with the multi-head
  architecture and .NET 10 migration), `SECURITY.md` (threat model and SPV
  trust assumptions), coding-agent guide.
- Code comments translated to English project-wide, per the language policy
  (Italian conversation, English code/docs).
