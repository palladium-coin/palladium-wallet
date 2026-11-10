# AGENTS.md

This file provides guidance to coding agents (OpenAI Codex and others following the AGENTS.md convention) when working with code in this repository.

> **Sync rule:** `CLAUDE.md` (read by Claude Code) carries the same content as this file, differing only in this header block. Any edit here must be mirrored there in the same commit.

## Role

Operate as an **expert in cryptocurrencies and cryptography**: reason with domain rigor about UTXO consensus, HD key derivation (BIP32/39/SLIP-132), signature schemes and scripts (P2PKH/P2SH/P2WPKH, PSBT), address encoding (base58/bech32), Merkle/SPV proofs, and at-rest encryption. When a choice touches cryptographic correctness or fund safety, judge it through that lens and flag known risks (nonce reuse, missing validation, exposed keys/seed, wrong fee/coin-selection, unverified server responses). Explain trade-offs with technical precision; never take for granted what hasn't been verified.

## Language policy

- **Conversation with the user**: Italian.
- **Code, comments, commit messages, documentation files**: English only.

## How to assist

Before implementing any requested change, judge whether it makes sense and say so plainly. Useful and consistent with the project → proceed. Useless, redundant, already covered elsewhere, or likely to degrade the code → say so with a short rationale and propose the better alternative (or doing nothing). No automatic agreement — an honest opinion is worth more than blind execution.

After implementing a **new feature**, propose the tests needed for proper coverage (unit tests for the new logic, edge cases, error paths; property-based tests where invariants apply; integration tests against `FakeElectrumServer` if network/SPV code is involved; a fuzz target if a new untrusted-input parser was added) — don't just write the feature and stop there.

## What it is

SPV wallet (Sparrow-style) for the **Palladium (PLM)** cryptocurrency, a Bitcoin-derived UTXO chain. Targets desktop (Windows/Linux) and Android from the same source. Lightning is excluded from the first release.

## Stack and structure

.NET 10 + Avalonia UI 12 + NBitcoin.

```
src/Core/         Chain/ Crypto/ Wallet/ Spv/ Net/ Storage/  (no UI dependency)
src/App/          shared Avalonia UI library (App, Views, ViewModels, Loc, Assets)
src/App.Desktop/  desktop head (WinExe): Program.cs, app.manifest, .ico → runnable
src/App.Android/  Android head (net10.0-android): MainApplication/MainActivity → apk
src/Cli/          CLI on the same Core            tests/   xUnit
docs/             docs-as-code process + ADRs (one per feature), checked by docs/check-docs.sh
docker/           reproducible release builds (build.sh + pinned Dockerfiles) → dist/
```

- The Avalonia UI lives **once** in `src/App` (a library); the two heads only carry the per-platform entry point and packages.
- `MainView` (UserControl) is the shared root, hosted by `MainWindow` on desktop and as the single-view root on Android.
- **Non-negotiable dependency rule:** `App`/`Cli` depend only on `Core`; the UI reaches network/cryptography only through the wallet domain, never directly. `Core` knows nothing about the UI.

## Commands

.NET 10 SDK lives in `~/.dotnet` — in non-interactive shells, before any `dotnet` command run
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`.

- **Build:** `dotnet build`
- **Test** (headless, primary verification layer): `dotnet test`
  - Single test: `dotnet test --filter "FullyQualifiedName~TestName"`
  - Property-based tests (CsCheck, `PropertyTests.cs`) run in the same command, ~30s
  - Coverage: `dotnet test tests/PalladiumWallet.Tests --settings tests/coverage.runsettings --collect:"XPlat Code Coverage"` (coverlet, Cobertura XML); CI gates `Core` with `tests/check-coverage.py` (95% lines / 88% branches)
  - ViewModel tests are headless (`Avalonia.Headless` via `UiSession`, xunit 2) and CLI tests invoke the real entry point in-process: tests under `tests/PalladiumWallet.Tests/App/` that touch process-wide state (`AppPaths`, `Loc`, `Console`, `MainWindowViewModel.UpdateCheck`/`ProfileOverride`) go in xUnit collection `"AppPaths"`; never let a test reach GitHub or a real indexing server
  - Test tree mirrors `src/Core` (`Chain/ Crypto/ Net/ Spv/ Storage/ Wallet/`) — put new tests in the folder matching the code under test
  - Network/SPV code (`ElectrumClient`, `WalletSynchronizer`, `TransactionInspector`, TOFU pinning): test against the in-process fake server `tests/PalladiumWallet.Tests/Net/FakeElectrumServer.cs` (loopback TCP + optional TLS, per-method handlers, call counters) — extend that, don't mock the client (it isn't an interface, by design)
  - Two `internal` test seams (via `InternalsVisibleTo`) sandbox the remaining externals: `UpdateChecker.CheckAsync` takes an optional `HttpMessageHandler` (never hit GitHub from a test); `AppPaths.BootstrapDirOverride`/`PortableBaseOverride`/`DefaultRootOverride` redirect the machine-global path locations (tests using them share xUnit collection `"AppPaths"` because that state is static)
- **Live server probe** (`tests/PalladiumWallet.ServerProbe`): read-only conformance/health check for real Electrum/Fulcrum/ElectrumX servers (`dotnet run --project tests/PalladiumWallet.ServerProbe -- --server host:port --ssl`, or `--registry`). Also runnable from `dotnet test` via env vars: `PALLADIUM_TEST_SERVERS=host:port,… [PALLADIUM_TEST_SSL=1] [PALLADIUM_TEST_NET=…] dotnet test --filter LiveServer` (no-op when unset, so the default run stays offline)
- **Regtest end-to-end** (`tests/regtest/e2e.sh`): starts its own Palladium Core + plm-fulcrum containers (the node enforces relay policy with `-acceptnonstdtxn=0`) and drives the CLI through sync, dust refusal/boundary, broadcast and coin freezing, taproot and WIF wallets, PSBT interoperability with the node as the external creator/signer, 2-of-3 multisig (native and P2SH-wrapped) against the node's own addresses, and the GUI view model against the real node (incl. a lying proxy, `LyingProxy`); needs Docker, ~75s, not part of `dotnet test`
- **Fuzzing** (`tests/PalladiumWallet.Fuzz`, SharpFuzz): one target per untrusted-input parser (`header merkle slip132 bip39 address coinamount walletdoc encfile peers headerrange cosignerkey psbt multisigconfig`), each encoding that parser's error contract — any other escaping exception is a finding
  - Seed corpus (incl. regression inputs for past findings) replays inside `dotnet test` via `FuzzCorpusTests`
  - Quick smoke without tooling: `dotnet run --project tests/PalladiumWallet.Fuzz -- <target> --random 50000`
  - Coverage-guided campaign: `tests/PalladiumWallet.Fuzz/fuzz.sh <target>` (needs afl++ + the SharpFuzz.CommandLine tool)
  - After fixing a finding: add the crashing input to `SeedCorpus` in the fuzz project's `Program.cs` and regenerate with `--make-seeds Corpus`
- **GUI hot reload:** `dotnet watch --project src/App.Desktop` (on WSL2/WSLg the window shows on the Windows desktop, no graphics dependencies to install)
- **CLI:** `dotnet run --project src/Cli -- <command>` (no args → usage)
- **CI** (`.github/workflows/ci.yml`, ADR 0032): on every push to `main` and every PR it runs the offline test suite on Ubuntu and Windows, a fuzz smoke per parser target, the regtest end-to-end (Docker), and `docs/check-docs.sh`. Keep `dotnet test tests/PalladiumWallet.Tests` green locally first; the Android head is deliberately not built in CI
- **Release binaries (all 3 targets):** `./docker/build.sh [windows|linux|android|all]` — reproducible Docker builds (toolchain pinned in `docker/Dockerfile.*`, no SDK needed on host), artifacts in `dist/`, version taken from the App csproj (details in `docker/README.md`)
  - Single-file desktop publish needs `-p:IncludeNativeLibrariesForSelfExtract=true` or Avalonia's native libs (Skia/HarfBuzz/ANGLE) stay outside the exe and it silently fails to start
  - Android workload dictates the SDK API level — error XA5207 → bump `ANDROID_SDK_PLATFORM` in `Dockerfile.android`
  - Android release builds need a persistent signing keystore, generated once with `docker/keystore/generate-keystore.sh` (never commit it, see `docker/keystore/README.md`) — without it every build gets a random signature and users must uninstall to update instead of updating in place
- **Manual publish:** `dotnet publish src/App.Desktop -r win-x64|linux-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --self-contained` (AppImage via PupNet Deploy is a future step, no pupnet.conf yet)

**Android (apk):** needs the `android` workload (`dotnet workload install android`), a JDK (`JAVA_HOME`), and the Android SDK (`ANDROID_HOME`, or pass `-p:AndroidSdkDirectory=...`; a plain solution-level `dotnet build` needs it too).
- Provision once: `dotnet build src/App.Android -t:InstallAndroidDependencies -p:AndroidSdkDirectory=$HOME/android-sdk -p:AcceptAndroidSDKLicenses=true`
- Debug apk: `JAVA_HOME=<jdk> dotnet build src/App.Android -c Debug -t:SignAndroidPackage -p:AndroidSdkDirectory=$HOME/android-sdk` → `src/App.Android/bin/Debug/net10.0-android/*-Signed.apk`
- Head is an app, not a library (`<OutputType>Exe</OutputType>`); min SDK 23 (AndroidX requirement)

**CLI** (`src/Cli`): `create`/`restore`/`restore-xpub`/`info`; `sync`/`send`/`servers`/`reset-certs` (`--server host:port [--ssl]`); `newseed`/`addresses`; `utxos`/`freeze`/`unfreeze` (coins), `restore-wif`; PSBT and multisig: `sign-psbt`, `combine-psbt` (files `.psbt`, base64 or hex; `--psbt-out`), `cosigner-key`, `create-multisig` (`--wrapped`, `--config`, `--expect-id`), `export-multisig`. Default wallet file `~/.palladium-wallet/<network>/wallets/default.wallet.json` (`--file` to change it).

## Architecture (points that require reading multiple files)

- **Layers:** GUI → wallet domain → SPV/Sync → Network → Cryptography → Persistence; each layer depends only downward.
- **Network profile:** all chain constants (address prefixes, BIP32 headers, bech32 HRP, genesis, ports, coin_type 746) **centralized in `Core/Chain`** (`ChainProfiles`/`PalladiumNetworks`), selectable per network (mainnet/testnet/regtest). No scattered magic numbers.
- **LWMA / skip PoW:** LWMA difficulty, 2-minute blocks; an SPV client cannot recompute it → `SkipPowValidation = true`, trust anchored to **hardcoded checkpoints**. This is a custom layer: NBitcoin assumes Bitcoin's retargeting.
- **NBitcoin vs custom:** NBitcoin covers the custom network, BIP32/39, addresses, transactions, PSBT, signing, encoding, hashing — **do not reimplement these**. Hand-written custom code: JSON-RPC client for the indexing server (ElectrumX-like); SPV sync with Merkle verification; header/checkpoint validation; coin selection and fee policy; versioned encrypted JSON wallet file.
- **PSBT-centric:** every signing flow goes through PSBT (offline/air-gapped/multisig/hardware).
- **Ports:** 50001/50002 = indexing server (what the SPV wallet talks to), **not** the node's P2P port (2333).

## GUI conventions (`src/App`)

- **Shared `MainView` + heads:** the whole UI is a single `MainView` (UserControl), working both as a desktop window's content and as Android's single-view root.
  - Top-level APIs (file/folder picker, clipboard) are reached via `TopLevel.GetTopLevel(this)` since a UserControl doesn't expose them.
  - `MainWindowViewModel.IsDesktop` (from `OperatingSystem.IsAndroid()`) hides filesystem-only features (open-from-file; the data-location wizard step auto-skips on Android because the head sets `AppPaths.OverrideDataRoot`).
- **Single ViewModel** `MainWindowViewModel` (CommunityToolkit.Mvvm: `[ObservableProperty]`, `[RelayCommand]`); `Core` is driven directly from here.
  - Split into **partial classes by feature** (`MainWindowViewModel.Send.cs`, `.Receive.cs`, `.Sync.cs`, `.Settings.cs`, `.Wizard.cs`, `.Contacts.cs`, `.Update.cs`, …): new feature logic goes in the matching partial (or a new one), not in the main file.
- **In-app overlays, not OS windows:** details (address, transaction), settings, and help are full-screen `Border`s gated by an `IsXxxOpen` flag, not separate `Window`s — instant open/close, mobile-friendly, and popups/top-levels are slow on WSLg.
  - Pattern: bool property + Open/Close commands + backdrop handler and Esc key in `MainView`'s code-behind; overlay close buttons bind via `$parent[UserControl]` (not `$parent[Window]`, absent on mobile).
  - Heavy network work runs off the UI thread (`Task.Run`) so the overlay never freezes.
- **Localization:** `Localization/Loc.cs`, key→7 languages dictionary (it/en/es/fr/pt/de/zh); in XAML `{Binding Loc[key]}`, in C# `Loc.Tr("key")`. On language change the `Loc` instance is replaced. The language picker in the Settings overlay (`MainView.axaml`) is a hand-written list of `RadioButton`s bound to `IsLangXx` properties in `MainWindowViewModel.Settings.cs` — it does **not** read `Loc.Languages` dynamically, so a new language needs a button + property added there too, not just a dictionary column.
- **App version:** single source is `<Version>` in `src/App/PalladiumWallet.App.csproj`, read at runtime (`MainWindowViewModel.AppVersion`) and shown in the title. `Core/Net/UpdateChecker.cs` compares it against the latest GitHub release on startup; `MainWindowViewModel.Update.cs` prompts the user if newer.
- **Storage paths:** `Core/Storage/AppPaths` resolves data locations; `AppPaths.OverrideDataRoot` (top priority) is the per-platform seam — the Android head sets it to the app sandbox (`Context.FilesDir`), desktop leaves it null.

## Working rules

- **Cross-implementation tests:** compare addresses, txids, and PSBTs against a reference wallet (golden vectors). A different address or txid is a blocking bug.
- **Security:** seed and private keys never in plaintext on disk/logs/network; every server response validated with Merkle + checkpoints; watch-only truly read-only. `SECURITY.md` is the published threat model (SPV trust boundaries, encryption parameters, key handling) — any change to crypto, SPV validation, or key/seed handling must keep it accurate in the same commit.
- **Docs stay in sync (docs-as-code):** whenever a feature is added or an existing one changes, update the documentation in the same commit — `docs/` (see `docs/docs-as-code.md`), plus `README.md`, `USERGUIDE.md`, `SECURITY.md` and `CHANGELOG.md` where affected.
  - Every new feature gets an ADR in `docs/adr/` (from `docs/adr/template.md`, listed in `docs/adr/README.md`); a changed decision gets a new ADR that supersedes the old one, never a rewrite of an Accepted ADR
  - Run `./docs/check-docs.sh` before committing; it must pass (ADR numbering/structure, index, cited code paths, links, CLAUDE.md/AGENTS.md sync)
- **Releases:** `./update-version.sh` (interactive) updates `<Version>` in the App csproj (single source), mirrors `ApplicationDisplayVersion` in the Android head, increments `ApplicationVersion` (Android versionCode, **must strictly increase** or users can't update in place), and stubs a `CHANGELOG.md` entry
  - Fill in the changelog entry before/with the tag — it's the technical record of what shipped, not optional bookkeeping — then commit and tag manually
