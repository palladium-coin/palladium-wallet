# Changelog

Technical changelog for PalladiumWallet. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/); entries are grouped
by subsystem rather than strictly by date, since `0.9.0` is the first
release and covers the full history from the initial commit.

## [1.2.0] — 2026-11-03

Hardens SPV verification (header anchoring fix, recent-block witness cross-check, chain identity
check on connect), makes the network layer fail fast on dead or hostile servers, adds server fee
presets and safer CLI password handling, and fixes a Windows-visible sync race. Also adds a
linux-arm64 build target, CI on GitHub Actions and a much larger automated test suite.

### Documentation

- Docs-as-code process: `docs/` with one ADR per feature, checked by `docs/check-docs.sh`;
  the extended test strategy is documented.

- README: the multi-network claim now states that the GUI is mainnet-only and that testnet/regtest
  (CLI only) need an explicit `--server`.

### Added

- `linux-arm64` reproducible desktop build target (`./docker/build.sh linux-arm64`), cross-published
  from the x64 image, for Raspberry Pi and other 64-bit ARM boards.
- CI on GitHub Actions (ADR 0032): offline test suite on Ubuntu and Windows, fuzz smoke per parser
  target and docs checks, on every push to `main` and every PR.
- `tests/PalladiumWallet.ServerProbe`: read-only conformance/health check for real
  Electrum/Fulcrum/ElectrumX servers, also runnable from `dotnet test` via environment variables.
- Recent blocks (above the newest checkpoint, which cannot be anchored) are now cross-checked with
  a second server: the header at the same height must be identical. The witness is taken only from
  the hardcoded bootstrap servers, one extra pinned connection opened on demand. A disagreement at
  3+ blocks of depth aborts the sync; a shallower one leaves the proof unverified until the next
  sync (possible reorganisation); with no reachable witness behaviour is unchanged. A bounded
  proof-of-work check was evaluated and rejected (see ADR 0042).
- History rows and the transaction detail now flag a confirmed transaction in a block above the
  newest hardcoded checkpoint as **not anchored** (orange label with an explanatory tooltip).
  Such a block cannot be chained to a checkpoint (ADR 0040), so only the Merkle proof against the
  server's header backs it; the flag makes the reduced guarantee visible. Not shown on
  testnet/regtest, which have no checkpoints. See ADR 0041.
- Fee presets from the server: after connecting, the Send tab offers Fast, Normal and Economy
  rates from `blockchain.estimatefee` (2, 6 and 24 blocks) and shows them with the relay fee;
  Normal is preselected unless the user already typed a rate. Estimates are converted from
  PLM/kB to sat/vB, never go below the relay fee, and implausible values are ignored; without
  an estimate the manual field and the 1 sat/vB default remain. A manual rate below the relay
  fee is refused before building, and a fee above 1% of the amount sent is flagged in the
  preview. Applies to Send and Donate. See ADR 0039.

### Changed

- **CLI (behaviour change):** the wallet password no longer has to be a command-line argument.
  It can come from `--password-env NAME`, `--password-stdin`, or a no-echo terminal prompt
  (with a confirmation when creating); with no terminal and no password the command fails
  instead of hanging. `--password P` still works but prints a warning on stderr (shell history,
  process list). Plaintext storage is now an explicit opt-in: `create`/`restore*` without a
  password and without `--no-encryption` fail, where before they silently wrote the seed
  unencrypted. Scripts that relied on omitting `--password` must add `--no-encryption`.

### Security

- **SPV header anchoring fixed (important).** The chain of headers used to be walked *from* the
  nearest checkpoint below a transaction's height *up to* it. With proof of work skipped
  (LWMA), that proves nothing: a malicious server could fabricate a header for the block of a
  fake transaction and chain fake headers after a real checkpoint, and every link would pass.
  The anchor now goes the other way: the header must hash-chain *up to* the nearest checkpoint
  at or above its height, whose hash is hardcoded, which cannot be forged. This also covers
  blocks below the first checkpoint (heights < 20,000), which were previously unanchored. Blocks
  above the newest checkpoint still have nothing to anchor to (only the Merkle proof is
  checked): add a recent checkpoint at each release. A wallet cache from an older version is
  re-verified once (its "verified" claims are not trusted); headers and transactions are
  reused. See ADR 0040.
- Private keys are no longer precomputed for every address in the UI. Address rows and the
  address detail sheet carry only public data and derivation coordinates; the WIF is derived
  when the user reveals it (after the password) and dropped when hidden, when the sheet closes
  or when the wallet closes. Before, the WIF of every listed address sat in UI-bound strings for
  the whole session.

### Fixed

- **Sync result overwritten by a stale partial snapshot.** At the end of a sync the final verified
  snapshot was applied only after `await`ing the wallet save; a partial (not yet verified)
  snapshot still queued on the UI thread could run during that await, replace the cache and be
  displayed instead, leaving transactions shown as unverified after a completed sync. The run is
  now finalised and applied before the save. Deterministically visible on Windows, where the save
  really yields.
- Adding a contact now validates the address for the wallet's network and rejects duplicates
  (case-insensitive); whitespace and line breaks in a pasted address are removed. Before, any
  string was saved and the error only appeared at send time. Contacts already stored with an
  invalid address still load and display.
- The wallet now verifies the chain identity of a server right after connecting: the
  `genesis_hash` from `server.features` must match the profile and, on mainnet (which shares
  Bitcoin's genesis), the header at the highest checkpoint not above the server's tip must match
  the hardcoded one. A server on another chain is refused with an explicit message and the next
  known server is tried. Applies to the GUI, peer discovery, the CLI and the server probe.
- `ElectrumClient`: a single server reply is now capped at 16 MiB (`maxLineBytes` in
  `ConnectAsync`). A hostile server could stream an endless line with no newline and exhaust the
  client's memory; the connection now fails with `InvalidDataException` as soon as the cap is
  exceeded.
- `ElectrumClient`: every request is now bounded (30 s, 10 s for the handshake; configurable in
  `ConnectAsync`). A server that stops answering without closing the socket made a running sync
  wait forever; now the request fails with `TimeoutException`, the connection is marked dead
  (`Disconnected` is raised, pending and later requests fail fast) and the app reconnects.
- Fee rate parsing no longer depends on the system culture: on Italian/German systems `1.5`
  was read as `15` sat/vB (10x the intended fee). The Send form, the Donate form and the CLI
  `--feerate` now share `CoinAmount.TryParseFeeRate` (invariant culture, `.` or `,` as decimal
  separator, no thousands separators, range (0, 1000] sat/vB). Donate and the CLI no longer
  silently fall back to 1 sat/vB on an invalid value: they report an error.
- `ElectrumClient`: after a protocol error (e.g. a malformed line from the server) the read
  loop died but the client kept reporting `IsConnected == true`, and any later request waited
  forever for a response that could never arrive. The read loop now marks the connection dead
  before failing the pending requests, and `RequestAsync` fails fast with `IOException` on a
  dead connection.

### Testing

- Headless ViewModel tests (Avalonia.Headless, no display): wizard flows, password/lock handling,
  seed and private-key reveal gating, contacts, settings, sync and send against the fake server.
- In-process end-to-end CLI tests (create/restore/info/sync/send against the fake server).
- Localization consistency tests (7 languages, placeholders, keys used in sources exist).
- Error-path tests for `Core` (empty header range during anchoring, send-all limits, `SyncCache`
  roundtrip, derivation edge cases, corrupted config), concurrency tests (in-flight cap, disposal
  under load, wallet-lock contention).
- Coverage settings (`tests/coverage.runsettings`) and a coverage gate in CI (`Core`: 95% lines /
  88% branches); thread pool minimum raised in tests to avoid starvation; async UI test bodies are
  now driven to completion (they previously passed vacuously).
- Test seams: `MainWindowViewModel.UpdateCheck` and `MainWindowViewModel.ProfileOverride`
  (internal) so ViewModel tests never reach GitHub or a real indexing server.

## [1.1.0] — 2026-07-19

Adds a pure address-only watch-only mode (Core + CLI + App wizard + Send
PSBT export), makes SPV sync render balance/history progressively instead
of blocking on full Merkle verification, and fixes several Android
sync-reconnect bugs found by testing the previous fix on a large wallet.

### Added

- Pure watch-only wallets from one or more plain addresses, no extended
  key or private key material at all (unlike existing xpub/WIF imports,
  which can still derive/hold key material): `WalletDocument.WatchAddresses`
  + `WalletLoader.NewFromAddresses`, `ScriptKind` inferred from the address
  via `DerivationPaths.KindFor`, CLI `restore-address` command, and a
  matching setup-wizard step. `TransactionFactory` already refused to sign
  for any `IsWatchOnly` account, so the new address-only accounts inherit
  that guarantee for free.
- Send flow: base64 PSBT export box + copy button and a visible warning
  banner for watch-only accounts, so the unsigned PSBT built from a
  watch-only wallet can actually be taken elsewhere to sign (previously
  only the CLI printed it).
- Chinese (Simplified) as a 7th UI language (`Loc.Strings`/`Languages`);
  the Settings language picker is a hand-written `RadioButton` list, not
  generated from `Loc.Languages`, so `IsLangZh` was added to
  `MainWindowViewModel.Settings.cs` and `MainView.axaml` too.
- New mainnet checkpoint at height 475124 (`ChainProfiles`).

### Changed

- SPV sync now renders balance/history as soon as transaction downloads
  finish instead of blocking on every historical Merkle proof — critical
  on mobile, where proof-checking can take much longer than the download.
  Proofs keep verifying in the background and each transaction's
  `Verified` flag catches up progressively; header ranges are fetched in
  batches (`blockchain.block.headers`) instead of one call per header.
  Coin selection (`UtxoSpendability.IsSpendable`) still refuses to spend a
  UTXO until its Merkle proof is actually checked, regardless of
  confirmation count — a server fabricating a confirmed balance can get it
  displayed early but never spent before the forgery is caught. The disk
  cache only ever persists the fully-verified end state. UI surfaces the
  new `PendingVerificationSats`/`SpendableSats` split with a
  "verifying..." badge.
- `ElectrumClient`'s in-flight request cap (`MaxInFlight`, previously a
  hardcoded 32) is now an optional `ConnectAsync` parameter, since
  different indexing servers tolerate different concurrency before
  throttling.

### Performance

- Checkpoint-anchoring state (`_anchoredUpTo`) is now persisted across
  sync sessions via `SyncCache` instead of being re-walked and
  re-verified from scratch on every app restart, even when the header
  bytes were already cached on disk — this dominated reconnect time on
  large wallets.

### Fixed

- `TransactionFactory.Build`: sending a large amount from a wallet with
  many small UTXOs could produce a transaction over the standard 100 KvB
  relay limit, previously surfaced only as a cryptic
  `Transaction's size is too high` error after everything else had
  already succeeded. UTXOs are now ordered largest-first with a binary
  search for the smallest spendable prefix, naturally preferring big
  coins over dust; NBitcoin's own oversized-selection case is now
  recognized and translated into a clear, actionable error instead of
  being read as "insufficient funds".
- Android: a connection killed silently while the phone was locked (Doze,
  radio suspend, NAT timeout) still reported `IsConnected == true`
  because `TcpClient.Connected` only reflects the last known socket
  state, and the keep-alive ping's failure was swallowed by an empty
  catch — sync kept retrying on a dead socket instead of reconnecting.
  Fixed across two passes:
  - A failed keep-alive ping now tears down the client and reconnects;
    `OnPause`/`OnResume` on the Android activity force an immediate
    health check on resume instead of waiting for the 20s timer (itself
    liable to be suspended during Doze).
  - The keep-alive ping had no timeout, so a half-open TCP connection
    (the common outcome of a longer Doze suspend) left it awaiting a
    response that never arrives, so the teardown path was never reached
    — bounded to 8s, plus a re-entrancy guard against overlapping ticks.
  - Resume checking bailed out whenever a sync was already in progress,
    leaving a lock/unlock during an active sync hung indefinitely; it now
    cancels the stuck sync and tears down the dead client instead.
  - `WalletSynchronizer.ExportCaches` persisted raw transaction bytes only
    for already-verified transactions, discarding anything downloaded but
    not yet proof-verified when a sync was interrupted — forcing a full
    re-download on every resume of a large wallet. Confirmed txids are
    now tracked at download time, independent of verification status.
- Wizard/overlay `TextBox`es (wizard steps, private-key prompt, wallet
  info overlay) shrank the whole panel when clicked into empty: their
  `HorizontalAlignment="Center"` + `MaxWidth` containers sized from
  content `DesiredSize`, and `PlaceholderText` only contributes to that
  measurement while empty and unfocused. Switched to
  `HorizontalAlignment="Stretch"`, which sizes from the available arrange
  rect (clamped by `MaxWidth`) regardless of focus/placeholder state.
- Help overlay's Donate tab is now gated behind `IsWalletOpen`, like the
  rest of the wallet-only UI — it requires an open wallet to send from,
  so showing it earlier was a dead end.

### Documentation

- `USERGUIDE.md`: documents why initial sync scales with transaction
  count rather than wallet age (one Merkle-proof round trip per confirmed
  transaction, no batching in the Electrum-style protocol), why later
  syncs are fast (cache persists proofs/headers/anchoring state), and
  warns against using this wallet as a mining payout address (many small
  transactions measurably slow sync).
- `SECURITY.md`: documents address-only watch-only wallets, and adds
  multisig to the explicit "known limitations" list (unsupported —
  derivation for it throws, not just unimplemented UI).
- `README.md` reconciled with current repo state: 7 UI languages (was
  6, missing Chinese Simplified), watch-only described as xpub *and*
  address-only (was xpub-only), multisig no longer overclaimed as a
  working PSBT flow, CLI quick-reference includes `restore-address` and
  `servers`, links the new `USERGUIDE.md`.

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
