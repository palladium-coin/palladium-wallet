# Changelog

Technical changelog for PalladiumWallet. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/); entries are grouped
by subsystem rather than strictly by date, since `0.9.0` is the first
release and covers the full history from the initial commit.

## [Unreleased]

### Changed

- **Layout and flow**: the desktop window is resizable (default 1020×720, minimum 760×600); the Prepare / Confirm / Sign-a-PSBT buttons are pinned under the Send form the multisig wizard step has a pinned Back / Next footer and the phone Receive tab a pinned Copy button with the review of the prepared transaction right above them (headline in bold: what is paid and to whom; the fee and change below); the recipient box says what it holds as you type (*Valid address · P2WPKH*, or why not); the balance shrinks to fit a narrow phone instead of being clipped; wizard flows show a step counter ("Step 2 of 4") and the title is aligned with the content; a wizard validation message appears as an amber notice under the title, and a rejected contact is explained under the Contacts form; the two password fields have labels. New strings (`wiz.step`, `wiz.password.label`, `wiz.password.confirm.label`, `send.to.ok`) in 7 languages.
- **Transaction details and dark mode** (styles, no logic): cards inside an overlay are flat recessed groups instead of a second sheet of glass; slim translucent scrollbars with the content kept clear of them; own addresses in the input/output lists use a theme-aware green (`SuccessTextBrush`) instead of a fixed `MediumSeaGreen` (the unused `MineColorConverter` is removed); the Coinbase badge is a soft amber tint with amber text instead of white on a solid fill; muted text and section headings are brighter on the dark ground; light-theme secondary buttons get a visible hairline so they stay visible on overlays.
- **Theme setting** (Settings → Theme: System / Light / Dark): applied at once, saved in `config.json` (`Theme`, default `system`, an unknown value counts as `system`), restored at startup; 4 new strings in 7 languages.
- **Liquid-glass look** (styles only, Theme.axaml/Controls.axaml): a blue-violet-teal gradient ground, translucent glass cards and overlays with a lit-edge rim (`GlassBrush`, `GlassBorderBrush`, `GlassInsetBrush`, `OverlayCardBrush`), 20–24 px radii, glass secondary buttons and a top-lit gradient on accent buttons. Avalonia has no backdrop blur (only `BlurEffect` on an element's own content), so the translucency is over a coloured ground rather than a blurred view of the content behind; overlays stay ~97% opaque (lower lets the content behind ghost through the text) to keep text legible. There is no reduced-transparency switch (Avalonia does not expose the OS setting).
- **GUI visual polish** (styles only): buttons scale down on press and ease back, list rows fade their hover background, headings and the balance use size-specific letter spacing, every overlay card shares one `overlay-surface` style (larger radius, deeper soft shadow), and the main action of the start, Send and Contacts screens uses the accent style. Secondary buttons are now a white (light) or slate (dark) surface with a visible border instead of Fluent's flat grey, which read as disabled.
- **Receive tab**: the two columns align at the top, the QR panel has a hairline border so it reads on the white light-theme card, the Copy button confirms in place ("Copied", 1.8 s) instead of only in the status bar, and the phone layout is tighter so the Copy button is visible without scrolling at 400×800. Tapping the address box or the QR also copies, and the derivation path of the shown address (e.g. `m/84'/746'/0'/0/0`) is printed under it.
- The dust relay rate (3 sat/vB) moved from a literal in `TransactionFactory` to `ChainProfile.DustRelaySatPerVByte`; the thresholds (P2PKH 546, P2SH 540, P2WPKH 294, P2WSH 330 sat) were verified against Palladium Core v2.1.0 with `testmempoolaccept` (ADR 0049).

### Added

- **Desktop release binaries are built in CI**: publishing a GitHub release runs `.github/workflows/release.yml`, which builds Windows x64, Linux x64 and Linux ARM64 with the same `docker/build.sh` used locally, checks that the tag matches `<Version>`, and attaches the binaries, `SHA256SUMS` and a build-provenance attestation to the release. Android stays a local, locally-signed build uploaded to the release afterwards. `SECURITY.md` has a supply-chain section (what the checksums and attestation do and do not protect against) and `USERGUIDE.md` tells users how to verify a download. Not run on GitHub yet (ADR 0065).

- **`--net mainnet|testnet|regtest` for the desktop GUI** (`dotnet run --project src/App.Desktop -- --net regtest`): mainnet stays the default, a wrong name is refused, and the window title shows `[REGTEST]`/`[TESTNET]`. `tests/regtest/up.sh` and `down.sh` start and remove a throw-away node + plm-fulcrum to try the GUI against (ADR 0064).
- **Animated-QR (BC-UR) transport of PSBTs** for air-gapped signers: `Core/Wallet/Ur` (bytewords, CBOR subset, fountain code), *Show as animated QR* on the pending and the signed PSBT, and on Android a scanner that collects `ur:` frames into the PSBT box. Fuzz target `ur`. Frames match byte for byte those of foundation-ur-py (golden vectors in the tests); not tried with a real device; the Android scanner change was not built here (ADR 0063).
- **Sign a PSBT in the GUI** (Send tab, *Sign a PSBT…*): paste or open a `.psbt` file, read the inputs, outputs, change and fee, sign, copy or save the result, and broadcast once the signatures are enough. A multisig wallet can now be completed entirely in the GUI; its information screen shows the descriptors and saves the configuration file, and the creation step loads one, takes a seed passphrase and an account number, shows the own key as a QR, and asks to retype a generated seed (ADR 0062).
- **Exchange with signers and other wallets**: binary `.psbt` files (read by `sign-psbt`/`combine-psbt`, written with `--psbt-out`), hex PSBT text, `export-multisig` (the Coldcard/Sparrow configuration text, or the descriptors with checksums) and `create-multisig --config FILE`. Fuzz targets `psbt` and `multisigconfig`; the first run found a non-canonical-varint `InvalidDataException` from NBitcoin on a binary PSBT, now mapped to `FormatException`. No real device, Sparrow or Electrum was available to try (ADR 0061).
- **P2SH-wrapped multisig** (`sh(wsh(sortedmulti(…)))`, BIP48 script type 1'): `--wrapped` on `cosigner-key` and `create-multisig`. Addresses and wallet ID checked against Palladium Core; a 2-of-3 wrapped spend was signed by two cosigners, broadcast and mined on regtest (ADR 0058).
- **Create a multisig wallet in the GUI**: a wizard flow for the threshold, the N keys, an optional own seed (typed or generated) and the wrapped switch, then a step that shows the wallet ID and asks for the one the other cosigners read; a different ID writes nothing (ADR 0059).
- The regtest end-to-end runs in CI (`regtest-e2e` job), and Donate is exercised against a real node through a test seam for its address (ADR 0060). A PSBT in the bare shape other wallets export (witness_utxo only, no scripts, no origins) is tested to sign and complete.
- **Wallet ID for multisig wallets** (the BIP380 descriptor checksum, equal to Bitcoin Core's): printed by `create-multisig`, shown by `info` and in the GUI wallet information, with `create-multisig --expect-id ID` refusing to write a wallet whose ID is not the one another cosigner saw. This is how the cosigners check that no key was swapped in transit (ADR 0057). A multisig wallet now signs only inputs that spend its own addresses.
- **M-of-N multisig wallets** (native SegWit P2WSH, BIP48 keys, `sortedmulti` addresses) from the CLI: `cosigner-key`, `create-multisig`, `combine-psbt`, and `sign-psbt` for a multisig PSBT. Addresses were checked against Palladium Core's descriptors; a 2-of-3 spend was signed by two cosigners and broadcast on regtest. The GUI opens and syncs such a wallet and prepares its PSBT (ADR 0056). Fuzz target `cosignerkey` for the pasted cosigner keys.
- Regtest checks against the real stack, in `tests/regtest/e2e.sh`: PSBT interoperability with Palladium Core as the external party (our PSBT signed by the node, the node's PSBT signed by us), and the GUI view model against a real node and plm-fulcrum, including the broadcast reconciliation through a proxy that lies about broadcasts (ADR 0055). The witness tip clamp is tested through the view model's own witness wiring.

- Fuzz target `headerrange`: the shape check of a `blockchain.block.headers` reply is now a parser of its own (`Core/Spv/HeaderRange.cs`, behaviour unchanged) with a seed corpus and a CI smoke job (ADR 0054).
- Tests for the paths that had none: the zero and invalid amount through the ViewModel (Send and Donate), a Prepare that fails without a field edit (relay fee rising after the user typed a fee), the broadcast-mismatch reconciliation for the Donate tab and for `sign-psbt --broadcast`, the witness connections closed when the primary is replaced, a notification for an already-used address, and the write-failure path of `ElectrumClient`.

- CLI `restore-wif` (import WIF private keys as a spendable wallet, `-` reads them from stdin) and `--kind taproot` (BIP86); taproot and WIF wallets were checked end to end on regtest: sync, spend, broadcast and mined by a node that enforces relay policy (ADR 0053).

- The total of the frozen coins is shown under the balance, so a forgotten freeze is visible; the Send tab shows the coin-control summary on the phone layout too (ADR 0052).

- CLI `utxos`, `freeze` and `unfreeze`: list the coins of the last sync and keep a coin out of every spend, offline, with the same flag the Coins tab uses (ADR 0051).

### Fixed

- **A send from a wallet of many small coins failed with "Invalid transaction: Transaction's size is too high"** (a miner paying 50 PLM from 3.125 PLM rewards): NBitcoin's default policy caps the *serialized* size, witness included, at 100,000 bytes, but the node's rule is a weight of 400,000, i.e. 100,000 vB, and a native-segwit input is 68 vB yet serializes to ~149 bytes. Anything above ~670 inputs was refused although the network takes it (an 800-input send-all of 54,242 vB, 119 KB serialized, was accepted by Palladium Core on regtest). The serialized cap is lifted and the existing virtual-size check (the node's own rule) applies. The coin search also no longer probes with thousands of inputs: it starts at the first prefix of the biggest coins whose total reaches the amount and gallops up from there, so 50 PLM from 4,000 rewards builds with 17 inputs.

- **Paying the same saved contact twice failed silently**: after a send the recipient box was cleared but the Send tab's contact picker kept showing the contact, so choosing that contact again changed nothing and the box stayed empty (the same after editing the box by hand or scanning a QR). The picker now empties whenever the box stops holding the contact's address. Covered by view-level tests of the real `ComboBox` and by regtest tests against a node and plm-fulcrum (`ContactsSendRegtestTests`: three contacts of different address types, the same contact twice, contacts added while the wallet resyncs, contacts in the wallet file), now part of `tests/regtest/e2e.sh`.

- The tab strip did not keep its one-row layout: the `UniformGrid` panel was set on the `TabStrip`, where the `TabControl` ignores it, so the six tabs wrapped (a sixth tab on a second row) on a narrow window. It is now set on the `TabControl`: six equal cells in one row at any width. Added a headless test of the mobile layout at 400px through `MainWindowViewModel.MobileLayoutOverride`.
- The About text claimed "seed and keys always encrypted", which is not true for a wallet created without a password (the wizard offers that, with a warning). It now says the seed and keys never leave the device and the wallet file is encrypted when a password is set (all 7 languages).
- Texts assembled from translated parts kept the language (and unit) of the moment they were made: the send and donation review ("Change:", "fee … vB"), the fee-estimate hint and the connection indicator ("connected") stayed in the old language after a language switch. They are now rendered from the prepared transaction, the fee estimates and the connection state, so a switch re-renders them; the review and the donation review also follow a unit change. The review's "fee … (… vB)" part and the donation's "watch-only" suffix, hard-coded in English, are translated (new Loc key `send.review.fee`).
- Stale status-bar messages: "The words do not match" stayed after going back in the wizard, "The two passwords do not match" after turning encryption off, and "Invalid address…" after the next contact was added successfully. The status is now cleared when the wizard step changes, when the encryption option is toggled, and when a contact is saved.
- An empty, malformed or wrong-network recipient in Send (and an invalid donation address) showed the parser's exception text (`Error: Invalid string`); it now says "Address not valid for this network".
- The buttons that reveal and hide an address's private key (and the password prompt in front of it) said "Show seed"/"Hide"; they now say "Show key"/"Hide key" (new Loc keys `addr.privkey.reveal` and `addr.privkey.hide`).
- Confirming a prepared send or donation could broadcast an incomplete transaction when the command was invoked while the Confirm button was disabled (watch-only wallet, or a multisig wallet holding one of two signatures); the command now refuses anything that is not fully signed and tells the user so ("Transaction not complete: more signatures are needed. Nothing was sent…") instead of doing nothing.
- A failed write on a server connection failed the pending requests before marking the connection dead, so a caller woken by the failure could still see it as connected; it is now marked dead first, as the read loop does.

- An unknown `--kind` value (a typo such as `taprot`) silently created a native segwit wallet; it is now an error.
- `info` printed an empty path and extended key for an imported-key wallet; it now says what was imported.

- Malformed CLI input and environment failures escaped as a stack trace (exit 134): an invalid `--path`, `--server` port or `--scan`, a server that refuses the connection, a corrupt wallet file. They are now one-line errors with exit code 1.

- The PSBT of a watch-only wallet could hold a different transaction (another output order) from the one whose txid and fee had just been shown: it is now built from that very transaction (ADR 0045).
- A server could report an inflated chain tip and make fresh outputs look deeply confirmed; the tip is now checked against a witness server and the lower one is used when they differ by more than 2 blocks (ADR 0045).
- A TLS connection made without a pin store accepted any certificate; it now validates the certificate normally unless the caller opts out explicitly (ADR 0045).
- The wallet file is written owner-only (0600 on Unix) and flushed to disk before the rename; before, a wallet saved without a password was readable by other local users, and overlapping `Save`/`SaveAsync` calls shared one temporary file (ADR 0044).
- The single-instance wallet lock is released and its file removed in one step (`DeleteOnClose`); before, a window after release let two processes own the same wallet.
- Servers announced by peers are no longer tried automatically as fallbacks, and peers naming `localhost` or private addresses, or ports outside 1–65535, are dropped (ADR 0044).
- Imported-key wallets (WIF or plain addresses) fetched their address histories concurrently into a shared dictionary, which could silently lose history entries; the results are now gathered before being stored.
- Importing the same key or address twice crashed the synchronisation (duplicate scriptPubKey) and listed it twice; duplicates are now collapsed on import and when loading an existing wallet file, keeping the copy that has the private key.
- A transaction reported at different heights by different addresses (a block arriving between the per-address queries) now takes the highest one instead of whichever was read last.
- Wrapped segwit (P2SH-P2WPKH) wallets could not build a spend: the redeem script is now attached to each input coin, so they can send, and watch-only ones can produce a PSBT.

- A zero amount was accepted for a payment, and a payment below the dust limit of its script (546 sat for P2PKH, 294 sat for P2WPKH, as Bitcoin Core computes it) was built and then rejected by the network; both are now refused before signing, in the watch-only path too.
- A broadcast reply naming a txid other than the one built was shown as a successful send; it is now reported as not confirmed.
- Addresses already used before the last sync were not subscribed on the server, so a payment to them was not notified until the next block.
- A failed write on the server connection left it looking alive until the 30 s request timeout; it is now torn down at once, and an exception thrown by a notification subscriber no longer drops the connection.
- Reconnecting after the primary server died left the witness connections of the previous session open.
- The Frozen flag of a UTXO is kept across syncs (it was reset on every sync). No UI sets it yet.
- Core error messages and comments are in English; the wrong-password message is shown in the UI language.
- `send` synchronizes the wallet before it builds the transaction. Before, it built from the last saved cache, so a second send right after the first could select an output already spent and fail at broadcast, after printing a txid.
- The CLI `send --broadcast` and `sign-psbt --broadcast` check that the server's txid is the one computed locally, as the GUI already did.
- When a broadcast is answered with another txid, the wallet no longer stops at an error: it asks the server for the transaction it built. If the server holds it, the send is done and the anomaly is reported; if not, the transaction stays pending and can be sent again (same signed bytes); if the check cannot be made, it stays pending too. The CLI exits 0 when the transaction is in the network, and otherwise exits 1 and prints the signed hex. See ADR 0047.
- In the GUI, changing the destination, amount, *Send all*, fee rate or coin selection after **Prepare** left **Confirm** active, so it sent the earlier transaction while the form showed new values. The prepared transaction is now a snapshot: any of those changes discards it and clears the summary, and the summary shows the amount and the full address the transaction really pays, and the change. A sync that finds one of its coins no longer spendable (or freezing one of them) drops it with a message. Same for the Donate tab. See ADR 0048.
- A watch-only transaction with legacy or wrapped inputs shows "txid not final until signed": its txid changes when signed, so the one shown before was not the one broadcast.
- A frozen UTXO stays frozen after a CLI sync, and it is excluded from the balance as well as from coin selection (the flag is applied while the UTXOs are built).
- An address of another network (e.g. a mainnet address on regtest) gives a one-line error instead of a stack trace.

### Added

- A **Coins** tab in the GUI lists every coin of the wallet. A coin can be frozen (it is then never spent, and it leaves the balance; the freeze is saved in the wallet file), and coins can be selected to restrict the next send or donation to them (coin control). See ADR 0046 and the user guide, section 8.7.
- `sign-psbt` CLI command: signs a PSBT with the wallet's keys after printing inputs, outputs (own change marked) and fee, refuses sighash types other than ALL and a previous transaction that does not match its outpoint, finalises when complete and can broadcast (ADR 0045).
- The xpub/xprv import step of the GUI takes the optional master fingerprint and account path (ADR 0045).
- PSBTs attach the full previous transaction of every input, so an offline signer can verify the amounts.
- Watch-only PSBTs now carry the BIP32 origin of the inputs, of the change output and of the account xpub when the master fingerprint is known, so an external signer can match its keys; `restore-xpub` accepts `--fingerprint` and `--path` (ADR 0044).

### Security

- A raw transaction served by the indexing server is now required to hash to the txid it was requested under (sync, preload from the wallet cache, transaction details, CLI `send`). Before, the Merkle proof covered only the txid, so a hostile server could return a fabricated transaction for a real txid and have it counted as verified (ADR 0043).
- Block headers not chained to a checkpoint (above the newest one, or on networks without checkpoints) are no longer kept between syncs nor written to the wallet file; proofs for blocks less than 24 deep are re-verified at every sync. This fixes honest servers being reported as untrustworthy after a shallow reorganisation, a state that also survived restarts (ADR 0043).
- Header-range replies are validated (count within what was requested, hex exactly `count × 80` bytes, valid hex) and a header reaches the cache only after the whole range has been validated against the checkpoint (ADR 0043).

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
