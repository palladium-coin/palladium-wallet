# Palladium Wallet

**An SPV wallet built specifically for the Palladium (PLM) cryptocurrency** and optimized for its chain. Runs on desktop (Windows/Linux) and Android from a single shared codebase.

Unlike generic wallets adapted to many coins, Palladium Wallet is designed around Palladium's consensus parameters — a Bitcoin-derived UTXO chain with 2-minute blocks and LWMA difficulty — and centralizes them in a single network profile. This keeps it lightweight, predictable and faithful to the chain: no client-side difficulty recalculation (trust is anchored to hardcoded checkpoints), mandatory Merkle verification on every confirmed transaction, and a network client written specifically for Palladium's indexing server.

## Features

- **Lightweight SPV**: syncs against an indexing server (ElectrumX-like protocol) without downloading the full chain.
- **Security**: seed and private keys encrypted on disk (AES-GCM, PBKDF2-SHA512), never in plaintext in logs or on the wire; every server response is validated with Merkle proofs + checkpoints.
- **HD wallet** (BIP39/BIP32), SegWit/wrapped/legacy addresses, watch-only from xpub or from plain addresses (no key material at all).
- **PSBT-centric**: signing flows go through PSBT (offline / air-gapped); watch-only wallets export an unsigned PSBT for offline signing. M-of-N **multisig** (P2WSH or P2SH-P2WSH, BIP48, `sortedmulti`) is supported from the CLI and the GUI wizard: cosigners exchange public keys, each builds the same wallet, and a PSBT collects the signatures (`create-multisig`, `cosigner-key`, `sign-psbt`, `combine-psbt`); addresses are checked against Palladium Core.
- **Multi-network**: mainnet, testnet, regtest in the core and the CLI (`--net`); the desktop GUI runs on mainnet unless started with `--net` (ADR 0064). Testnet and regtest ship without bootstrap servers or checkpoints, so pass `--server host:port`.
- **Cross-platform**: desktop (Windows/Linux) and Android share one Avalonia UI; a **CLI** runs on the same core.
- **Multilingual**: Italian, English, Spanish, French, Portuguese, German, Chinese (Simplified).

## Architecture

```
PalladiumWallet.sln
├─ src/Core/          Chain/ Crypto/ Wallet/ Spv/ Net/ Storage/  (no UI dependency)
├─ src/App/           shared Avalonia UI library (Views, ViewModels, Loc, Assets)
├─ src/App.Desktop/   desktop head (Windows/Linux) → runnable
├─ src/App.Android/   Android head → apk
├─ src/Cli/           CLI on the same Core
└─ tests/             xUnit
```

The UI is written **once** in `src/App`; the desktop and Android heads only add the per-platform
entry point and packages.

Stack: **.NET 10 + Avalonia UI 12 + NBitcoin**.

---

## Development environment

For desktop and the CLI you only need the **.NET 10 SDK**. The core and crypto are fully testable without the GUI or a real network.

### Windows

1. Install the .NET 10 SDK:
   ```powershell
   winget install Microsoft.DotNet.SDK.10
   ```
   (alternatively, the installer from <https://dotnet.microsoft.com/download/dotnet/10.0>)
2. Clone the repository and restore dependencies:
   ```powershell
   git clone https://github.com/palladium-coin/palladium-wallet.git
   cd PalladiumWallet
   dotnet restore
   ```

### Linux

1. Install the .NET 10 SDK through your distro's package manager, or without root via the official script:
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet10"
   export PATH="$HOME/.dotnet10:$PATH" DOTNET_ROOT="$HOME/.dotnet10"
   ```
   (add the two `export` lines to your `~/.bashrc` to make them permanent)
2. Clone and restore:
   ```bash
   git clone https://github.com/palladium-coin/palladium-wallet.git
   cd PalladiumWallet
   dotnet restore
   ```

> The GUI uses Avalonia, which runs natively on Windows and Linux with no extra graphics dependencies.

### Android (additional setup)

Building the apk also requires the Android workload, a JDK, and the Android SDK:

```bash
dotnet workload install android          # .NET Android build packs
# JDK 17+ must be available (set JAVA_HOME)
# Provision the Android SDK once into ~/android-sdk:
dotnet build src/App.Android -t:InstallAndroidDependencies \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:AcceptAndroidSDKLicenses=true
```

To run the apk on an emulator (instead of a physical device), see
[*Android emulator (developer setup)*](#android-emulator-developer-setup) below.

---

## Running it

### Desktop GUI in debug (Linux & Windows)

The desktop head runs the same way on both OSes (`Debug` is the default configuration). Run it from
the repo root:

```bash
dotnet run --project src/App.Desktop                 # single run (Debug)
dotnet watch --project src/App.Desktop               # with hot reload (edit XAML/C# and see changes live)
dotnet run --project src/App.Desktop -c Release      # to try the Release config
```

- **Linux** — runs natively on X11/Wayland, no extra graphics packages. On **WSL2** the window
  appears on the Windows desktop through WSLg (already working here, nothing to install).
- **Windows** — runs natively; use the same commands from PowerShell or a terminal.

The app writes its data under the per-user data folder (see *User guide → First launch*); delete it
to start from a clean first-run wizard.

### CLI

Same core, useful for scripts and headless environments:
```bash
dotnet run --project src/Cli -- <command>
```
Run without arguments for the full list of commands.

### Android

There is no `dotnet run` for a phone: build the apk and install it (see *Building → Android apk*),
or run it on an emulator (see *Android emulator (developer setup)*).

---

## Running tests

Tests are the **primary verification layer** — the core logic and crypto run headless, without the GUI or a real network.

Every push to `main` and every pull request runs the offline suite on Ubuntu and Windows, plus a fuzz smoke and the docs checks, via GitHub Actions (`.github/workflows/ci.yml`).

Run the whole suite:
```bash
dotnet test
```

Run a single test (or a group) by name:
```bash
dotnet test --filter "FullyQualifiedName~TestName"
```

Run only the tests in one project:
```bash
dotnet test tests/PalladiumWallet.Tests
```

Measure code coverage (the [coverlet](https://github.com/coverlet-coverage/coverlet) collector is already referenced; the report is a Cobertura XML under `TestResults/`). `tests/coverage.runsettings` excludes the test tooling, and `tests/check-coverage.py` applies the same gate as CI:
```bash
dotnet test tests/PalladiumWallet.Tests --settings tests/coverage.runsettings --collect:"XPlat Code Coverage"
python3 tests/check-coverage.py TestResults/*/coverage.cobertura.xml PalladiumWallet.Core:95:88
```

Run the tests against a real indexing server (opt-in, skipped when the variable is unset):
```bash
PALLADIUM_TEST_SERVERS=host:50002 PALLADIUM_TEST_SSL=1 dotnet test --filter LiveServer
```

> Cross-implementation tests compare addresses, txids and PSBTs against reference golden vectors: a different address or txid is a blocking bug.

### What the suite covers

The tests mirror the `Core` layout (`tests/PalladiumWallet.Tests/<area>/`):

| Area | What is verified |
|---|---|
| `Chain/` | Network profiles (prefixes, ports, coin type 746, SLIP-132 headers), NBitcoin network registration and the `INetworkSet` plumbing |
| `Crypto/` | BIP39 (official Trezor vectors, NFKD normalisation, all 8 supported wordlist languages), BIP32/44/49/84/86 derivation against public golden vectors, SLIP-132 encode/decode including corrupted-payload rejection, HD and imported-key accounts (fund-safety fallbacks for out-of-range indices), watch-only isolation |
| `Spv/` | Scripthash (vectors computed independently in Python), Merkle proofs (Bitcoin block 100000 + random trees), header parsing and PoW target validation, and the full **`WalletSynchronizer`**: gap-limit scanning, UTXO/history reconstruction, unconfirmed/immature balances, busy-retry, disk-cache reuse — including the paths where a lying server fails Merkle verification or serves a header chain that does not anchor to a hardcoded checkpoint, and the sync must abort |
| `Net/` | JSON-RPC transport (pipelining, error mapping, notifications, disconnection, cancellation, oversized responses), typed protocol wrappers, peer discovery/persistence, TLS trust-on-first-use pinning end-to-end (pin, match, mismatch, reset), the update check end-to-end via a stubbed HTTP transport (newer/equal/older tags, HTTP errors, malformed JSON, offline — all best-effort to null) |
| `Storage/` | AES-GCM encryption (roundtrip, tampering, fresh salt/nonce), wallet document schema/versioning, atomic saves, single-instance lock, data-path resolution with its full precedence chain (override → portable → pointer file → default) |
| `App/` | Headless ViewModel tests (Avalonia.Headless): wizard flows, password and file-lock handling, seed and private-key reveal gating, contacts, settings, sync and send against the fake server; in-process end-to-end CLI tests; localization consistency (7 languages, placeholders, keys used in sources exist) |
| `Wallet/` | Transaction building and signing (spendability rules, coinbase maturity, dust change, multi-UTXO selection, all standard destination types, watch-only PSBT flow, a golden txid for the PSBT signing path, standardness-policy rejection of absurd fees), transaction detail assembly (fees, mine/theirs attribution, RBF, coinbase), amount parsing/formatting, corrupted-wallet-file rejection |

Network-facing code is tested against an **in-process fake ElectrumX server**
(`tests/PalladiumWallet.Tests/Net/FakeElectrumServer.cs`): a real loopback TCP
socket speaking newline-delimited JSON-RPC (optionally TLS with a self-signed
certificate), with per-method handlers and call counters. Client, synchroniser
and inspector therefore exercise the same code paths used in production —
framing, retries, TLS pinning included — without any external dependency.

The suite also includes **property-based tests** ([CsCheck](https://github.com/AnthonyLloyd/CsCheck)) in `tests/PalladiumWallet.Tests/PropertyTests.cs`. These generate hundreds of random inputs per test and verify invariants that must hold universally — no crash on arbitrary strings, encrypt/decrypt roundtrip for any plaintext and password, SLIP-132 key roundtrip for every script kind and network, every leaf in a randomly-built Merkle tree verifies against its root. They run automatically with `dotnet test` and take ~30 s.

### Fuzzing

`tests/PalladiumWallet.Fuzz` fuzzes every parser that consumes untrusted input
(server-supplied headers/proofs/peer lists, wallet files, user-pasted
keys/mnemonics/addresses/amounts) via [SharpFuzz](https://github.com/Metalnem/sharpfuzz):
each target enforces the parser's documented error contract, so any other
exception escaping is a finding. The seed corpus — including a regression input
for every crash found so far — replays automatically inside `dotnet test`;
coverage-guided campaigns run separately with afl++ (`tests/PalladiumWallet.Fuzz/fuzz.sh`),
and a built-in random-mutation mode (`dotnet run -- <target> --random N`) needs
no external tooling. See `tests/PalladiumWallet.Fuzz/README.md`.

---

## Building

### Reproducible builds (Docker) — recommended for release binaries

All three distribution targets (Windows exe, Linux binary, Android apk) can be
built with one command inside Docker, with **no SDK installed on the host**:

```bash
./docker/build.sh          # interactive menu, or: ./docker/build.sh all
```

Why build this way: the whole toolchain is pinned in the Dockerfiles, so every
build uses exactly the same SDK versions regardless of the host machine — no
toolchain drift between releases — and the build environment itself is
reviewable in the repo. For a wallet this is a trust property, not a
convenience: anyone can rebuild the published binaries from source and check
they were produced by the process the repository declares.

When a GitHub release is published, CI builds the Windows and Linux binaries with this same script and attaches them
to the release with checksums and a provenance attestation; the Android APK is built and signed locally and uploaded
afterwards (ADR 0065).

See [docker/README.md](docker/README.md) for prerequisites, usage, how to run
each produced artifact, and troubleshooting. The sections below cover manual
builds with a locally installed SDK (the normal path during development).

### Development build

```bash
dotnet build                              # whole solution (debug)
dotnet build src/App.Desktop              # desktop head only
```

> A solution-wide `dotnet build` also builds the Android head, which needs the Android SDK
> (see *Android emulator (developer setup)* below). If you don't have it, build the specific
> non-Android projects (`src/App.Desktop`, `src/Cli`, `tests/...`).

### Desktop release (self-contained)

```bash
# Windows — single self-contained .exe (output: src/App.Desktop/bin/Release/net10.0/win-x64/publish/PalladiumWallet.exe)
# IncludeNativeLibrariesForSelfExtract embeds Avalonia's native libs (Skia, HarfBuzz, ANGLE):
# without it they stay as separate DLLs and the .exe alone silently fails to start.
dotnet publish src/App.Desktop -c Release -r win-x64 -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true --self-contained

# Linux — self-contained; AppImage then produced with PupNet Deploy
# (output: src/App.Desktop/bin/Release/net10.0/linux-x64/publish/PalladiumWallet)
dotnet publish src/App.Desktop -c Release -r linux-x64 --self-contained
```

[PupNet Deploy](https://github.com/kuiperzone/PupNet-Deploy) turns the Linux publish into an AppImage.
The executable is named `PalladiumWallet` (set via `<AssemblyName>` in the desktop head).

### Android apk

Prerequisites: the Android workload + SDK (see *Development environment → Android*). The Android
head already sets `<EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>`, so the apk is
**self-contained** and installs/runs standalone (a Fast-Deployment debug apk crashes at launch
with "No assemblies found" when installed without `adb`).

```bash
# Default debug apk — all ABIs (arm64-v8a + x86_64): runs on phones AND the x86_64 emulator.
# ~79 MB. Output: src/App.Android/bin/Debug/net10.0-android/*-Signed.apk
JAVA_HOME=<jdk-path> dotnet build src/App.Android -c Debug -t:SignAndroidPackage \
  -p:AndroidSdkDirectory=$HOME/android-sdk

# Smaller apk for a real phone — arm64 only (~41 MB):
JAVA_HOME=<jdk-path> dotnet build src/App.Android -c Debug -t:SignAndroidPackage \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:AbiArm64Only=true
```

The ABI restriction uses the `AbiArm64Only` flag, which is scoped to the Android head's
`<RuntimeIdentifiers>` in its csproj — do **not** pass `-p:RuntimeIdentifiers=android-arm64` on the
command line, it leaks to the `net10.0` projects (`Core`/`App`) and breaks the build. (The legacy
`AndroidSupportedAbis` property is deprecated and ignored.)

(Set `ANDROID_HOME` to skip the `-p:AndroidSdkDirectory` flag.) This debug apk is fine for personal
sideloading, but debug builds are signed with a key regenerated per build machine/container, so a
newer debug apk won't install over an older one without an uninstall first. For a stable signature
across releases (needed to update an existing install in place), build via
`./docker/build.sh android` instead — see [docker/keystore/README.md](docker/keystore/README.md).

> **Verification status.** The default multi-ABI apk is verified running on the x86_64 emulator
> (UI renders, connects to a server over TLS). The arm64-only apk builds correctly (41 MB,
> `arm64-v8a` only) but is meant for a physical arm64 phone — on the x86_64 emulator it only runs
> through slow ARM translation and stalls on the splash, so **verify it on a real device**.

### Version

The application **version** is set in a single place: the `<Version>` tag in
[`src/App/PalladiumWallet.App.csproj`](src/App/PalladiumWallet.App.csproj). It appears in the desktop
window title, in the Help dialog, and is stamped into the published binaries (and the apk's versionName).

---

## Android emulator (developer setup)

How to run the apk without a physical device. Paths assume the Android SDK in `~/android-sdk`
and a JDK at `JAVA_HOME` (JDK 17+). Tested on Linux / WSL2.

**1. Install the emulator, a system image and the matching platform** (once):
```bash
SDK=$HOME/android-sdk
$SDK/cmdline-tools/latest/bin/sdkmanager --sdk_root=$SDK \
  "emulator" "system-images;android-34;google_apis;x86_64" "platforms;android-34"
```
Use an `x86_64` image so the emulator runs with hardware acceleration (KVM); the app's min SDK is 23.

**2. Hardware acceleration (Linux/WSL2)** — the emulator needs access to `/dev/kvm`. Add yourself
to the `kvm` group once, then start a new shell (or prefix the launch with `sg kvm -c '…'`):
```bash
sudo usermod -aG kvm $USER
```
On WSL2, KVM must be enabled on the Windows host (nested virtualization); the emulator window is
shown on the Windows desktop through WSLg.

**3. Create an AVD** (virtual device):
```bash
echo no | $SDK/cmdline-tools/latest/bin/avdmanager create avd \
  -n plm -k "system-images;android-34;google_apis;x86_64" -d pixel
```

**4. Launch the emulator** (software GL is the most robust under WSLg):
```bash
$SDK/emulator/emulator -avd plm -gpu swiftshader_indirect -no-snapshot -no-audio &
$SDK/platform-tools/adb wait-for-device
# wait for full boot:
until [ "$($SDK/platform-tools/adb shell getprop sys.boot_completed | tr -d '\r')" = 1 ]; do sleep 2; done
```
If the window won't render, add `-no-window` and rely on `adb` + screenshots
(`adb exec-out screencap -p > shot.png`).

**5. Install and run the apk; capture logs to debug crashes:**
```bash
ADB=$SDK/platform-tools/adb
$ADB install -r src/App.Android/bin/Debug/net10.0-android/*-Signed.apk
$ADB shell monkey -p io.github.davide3011.palladiumwallet -c android.intent.category.LAUNCHER 1
$ADB logcat -d | grep -iE "monodroid|exception|fatal|avalonia"
```

**Automated smoke test** (ADR 0066): `python3 tests/android/smoke.py` installs the debug APK on a throw-away
AVD it creates and boots headless (or on a device that is already there, start-up check only), walks the start screen,
the restore wizard, the wallet home, Receive and Settings, and checks that the **System** theme follows the OS
light/dark switch while the app runs. It drives the accessibility tree, so it taps by label and reads text boxes
back (`adb shell input text` drops characters). It never uninstalls, and the full walk-through needs a device it booted
itself or `--reset-data`. Run by hand; CI does not build the Android head.

**VS Code "Android iOS Emulator" extension** (optional, click-to-launch): it only starts an
existing AVD, so create one first (step 3). Point it at the emulator binary:
```jsonc
// VS Code settings.json
"emulator.emulatorPathLinux": "/home/<user>/android-sdk/emulator"
// on WSL, use: "emulator.emulatorPathWSL": "/home/<user>/android-sdk/emulator"
```

---

## User guide (quick)

A condensed overview follows; for the complete, exhaustive walkthrough (every screen, every
validation rule, troubleshooting) see [USERGUIDE.md](USERGUIDE.md).

### First launch
1. On first launch (desktop), choose **where to store data** (wallet, configuration, certificates) — the default path or a folder of your choice. On Android this step is skipped: data lives in the app's private sandbox.
2. Create a new wallet, restore from seed, or open one of the wallets already in your data folder.
3. If you create a wallet, **write the seed phrase down on paper**: it will not be shown again. You can protect the file with a password.

> **Desktop vs Android.** The UI and features are the same on both. Differences: on Android the
> data-location step is skipped (fixed app sandbox) and *File → Open wallet from file* (importing a
> wallet from an arbitrary file) is hidden — open wallets from the in-app chooser instead. The
> version is shown in the desktop window title and, on every platform, in the Help dialog. The CLI
> is desktop/headless only.

### Main tabs
- **History** — list of transactions. *Double-click* (double-tap on touch) a row to open the full detail (amount, fee, addresses, sizes, confirmations).
- **Send** — recipient + amount (or "send all"), adjustable fee; for watch-only wallets a PSBT is produced to be signed offline.
- **Receive** — next unused address, with a **QR code** and a **Copy** button.
- **Addresses** — all derived addresses with balances; click for details (keys, derivation path).
- **Contacts** — address book with labels.

### Connection
- The status indicator at the bottom shows the connection to the **indexing server**; tapping it opens the server settings.
- Sync is SPV: it downloads only what concerns your wallet and verifies every confirmed transaction with a Merkle proof.

### Settings and Help
- **Settings**: language, display unit (PLM / mPLM / µPLM / sat), server.
- **Help**: software information and version.

### CLI in brief
```bash
# Wallet
dotnet run --project src/Cli -- create          [--words 12|24] [--kind segwit|wrapped|legacy|taproot] [--net mainnet|testnet|regtest] [--no-encryption]
dotnet run --project src/Cli -- restore         "<mnemonic>" [...]
dotnet run --project src/Cli -- restore-xpub    <slip132-key> [--net ...] [--no-encryption] [--fingerprint HEX8 --path m/...]
dotnet run --project src/Cli -- restore-wif     <wif1,wif2,...|-> [--kind ...] [--net ...] [--no-encryption]   # spendable, no seed
dotnet run --project src/Cli -- cosigner-key    "<mnemonic>" [--net ...]                                            # key to share with the other cosigners
dotnet run --project src/Cli -- create-multisig --threshold M --cosigner KEY ... [--wrapped] [--mnemonic "<words>"] [--expect-id ID]   # M-of-N P2WSH (or P2SH-P2WSH) wallet; the ID is what the cosigners compare
dotnet run --project src/Cli -- combine-psbt    <base64> <base64> ... [--broadcast]                                  # merge the cosigners' signatures
dotnet run --project src/Cli -- restore-address <addr1,addr2,...> [--net ...] [--no-encryption]
dotnet run --project src/Cli -- info            [--net ...]

# Network
dotnet run --project src/Cli -- sync     [--server host[:port]] [--ssl]
dotnet run --project src/Cli -- send     --to ADDRESS (--amount X | --all) [--feerate sat/vB] [--broadcast]
dotnet run --project src/Cli -- utxos     [--net ...]            # coins of the last sync, offline
dotnet run --project src/Cli -- freeze    <txid>:<vout>          # keep a coin out of every spend (unfreeze to undo)
dotnet run --project src/Cli -- sign-psbt <base64|-> [--broadcast] [--file wallet]
dotnet run --project src/Cli -- servers  [--discover]
```

The wallet password is read from `--password-env NAME`, `--password-stdin` or a no-echo terminal
prompt; a new wallet is stored unencrypted only with `--no-encryption` (`--password P` still works
but is unsafe and warns). See USERGUIDE section 17.
The default wallet file is `~/.palladium-wallet/<network>/wallets/default.wallet.json` (override with `--file`).
Run without arguments for the full command list (also covers `newseed`, `addresses`, `reset-certs`);
see [USERGUIDE.md §17](USERGUIDE.md#17-command-line-interface-cli) for complete flag reference.

---

## Changelog

Technical, per-version changes are tracked in [CHANGELOG.md](CHANGELOG.md).

## License

Released under the MIT License. See the [LICENSE](LICENSE) file.
