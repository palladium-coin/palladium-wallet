# PalladiumWallet — Reproducible Builds via Docker

This folder builds all three distribution targets — **Windows**, **Linux**,
**Android** — inside Docker containers. The entire toolchain (.NET 10 SDK,
JDK, Android SDK, android workload) lives in the container images, pinned in
the Dockerfiles, so:

- **anyone can produce the official binaries** with a single command, on any
  Linux machine, without installing any SDK on the host;
- **the toolchain never drifts**: every build uses exactly the same SDK
  versions, regardless of what is installed (or updated) on the host;
- **the build environment is auditable**: the Dockerfiles in this folder are
  the complete, reviewable definition of how release binaries are made — which
  matters for a wallet, where users must be able to trust that the shipped
  binary comes from the published source.

> **Scope note:** this pins the *build environment*. Bit-for-bit identical
> output across machines is a stronger property that .NET does not guarantee
> by default (embedded timestamps, signing); if two builds of the same commit
> differ, they differ only in those metadata, not in code.

---

## Prerequisites

A Linux host (native, WSL2, or a VM) with **Docker Engine** installed and
running. Nothing else — no .NET SDK, no JDK, no Android SDK.

```bash
# Check that Docker works:
docker info
```

If Docker is missing, install it from <https://docs.docker.com/engine/install/>
(or `sudo apt install docker.io` on Debian/Ubuntu). If `docker info` fails
with a permission error, add yourself to the `docker` group and start a new
shell:

```bash
sudo usermod -aG docker $USER
```

**Disk space:** ~2 GB for the desktop image, ~7 GB for the Android image
(SDK + emulator-less toolchain). **First-run time:** the images are built
automatically on first use — a few minutes for desktop, 10–20 minutes for
Android (large downloads). Subsequent builds reuse the cached images and take
well under a minute (desktop) / a few minutes (Android).

**Android release signing:** before the first `android` build, generate the
persistent signing keystore once — see [`keystore/README.md`](keystore/README.md):

```bash
./docker/keystore/generate-keystore.sh
```

Without it, `build_android` refuses to run — every APK must be signed with
the same key so future releases can update a previous install in place.

---

## Quick start

```bash
# From the repository root (or from the docker/ folder — both work):
./docker/build.sh
```

Running without arguments shows an interactive menu — pick a single target or
`all`. Non-interactive usage:

```
./docker/build.sh [TARGET] [--rebuild]

Targets:
  windows     Win x64 single-file executable (native libs embedded)
  linux       Linux x64 single-file binary (runs as-is, nothing to install)
  linux-arm64 Linux ARM64 single-file binary (runs as-is, nothing to install)
  android     Android APK (release-signed, prompts for keystore passwords)
  all         All targets above

Options:
  --rebuild   Force rebuild of the Docker images (needed after editing a Dockerfile)
```

Examples:

```bash
./docker/build.sh all               # build everything
./docker/build.sh windows           # Windows only
./docker/build.sh linux-arm64       # Linux ARM64 only
./docker/build.sh android --rebuild # Android, rebuilding the image first
```

---

## Output — and how to use each artifact

All artifacts land in `dist/` at the repository root. The version number is
read automatically from `<Version>` in `src/App/PalladiumWallet.App.csproj`.

| Target      | Path                                                     |
|-------------|-----------------------------------------------------------|
| Windows     | `dist/windows/PalladiumWallet-{ver}-win-x64.exe`          |
| Linux       | `dist/linux/PalladiumWallet-{ver}-linux-x64`              |
| Linux ARM64 | `dist/linux-arm64/PalladiumWallet-{ver}-linux-arm64`      |
| Android     | `dist/android/PalladiumWallet-{ver}.apk`                  |

**Windows** — a single self-contained `.exe` (runtime and native libraries
embedded). Copy it to any 64-bit Windows 10/11 machine and double-click.
The first launch takes a few extra seconds (it unpacks native libraries to a
per-user cache); later launches are normal. SmartScreen may warn because the
binary is not code-signed — choose "Run anyway".

**Linux** — a single self-contained binary, already executable. Copy and run:

```bash
./PalladiumWallet-{ver}-linux-x64
```

Works on any desktop distro with glibc, X11/Wayland and fontconfig (i.e.
effectively all of them); no .NET or other packages to install. If you
transfer it through a channel that strips permissions (e.g. a web download),
restore the execute bit with `chmod +x`.

**Linux ARM64** — same as above, cross-published for `aarch64` (e.g.
Raspberry Pi 4/5, ARM-based SBCs/laptops running a 64-bit distro). The build
runs on an x64 Docker host — .NET's self-contained publish cross-targets
`linux-arm64` without needing ARM hardware. Run it the same way:

```bash
./PalladiumWallet-{ver}-linux-arm64
```

**Android** — a release-signed APK for sideloading: transfer it to the phone
and open it (enable "install from unknown sources" if prompted), or install
via `adb install dist/android/PalladiumWallet-*.apk`. Supports Android 6.0+
(API 23), arm64 phones and x86_64 emulators.

> **Signature:** every APK is signed with the persistent keystore in
> `docker/keystore/` (see [Prerequisites](#prerequisites)), so installing a
> newer build over an existing one updates it in place — no uninstall, no
> data loss. This only holds as long as every release keeps using that same
> keystore file; see `docker/keystore/README.md` for the backup story.

---

## How it works

### Docker images

| Image               | Dockerfile           | Used for        | Size    |
|---------------------|----------------------|-----------------|---------|
| `plm-build-desktop` | `Dockerfile.desktop` | windows + linux + linux-arm64 | ~1.5 GB |
| `plm-build-android` | `Dockerfile.android` | android         | ~5 GB   |

Images are built automatically the first time a target needs them and reused
afterwards. Use `--rebuild` only after modifying a Dockerfile.

### Source isolation

The repository is mounted **read-only** inside the container; the build works
on a copy at `/tmp/build`. Your working tree is never touched — no stray
`bin/`/`obj/` directories, and a dirty working tree doesn't leak into the
build beyond the files it contains. Artifacts are written back through a
bind mount to `dist/` and chown'd to your user.

### NuGet cache

A Docker named volume `plm-nuget-cache` holds downloaded NuGet packages
across builds. To reclaim the space or force a clean re-download:

```bash
docker volume rm plm-nuget-cache
```

---

## Troubleshooting

- **`Docker daemon is not running`** — start it (`sudo systemctl start
  docker`; on WSL2, start Docker Desktop or the docker service).
- **Android image build fails downloading `commandlinetools`** — Google
  rotates the build number in the URL. Update the URL in
  `Dockerfile.android` to the current one from
  <https://developer.android.com/studio#command-tools> and rerun with
  `--rebuild`.
- **`error XA5207: Could not find android.jar for API level N`** — the .NET
  android workload moved to a newer API level. Bump
  `ANDROID_SDK_PLATFORM` (and `ANDROID_SDK_BUILD_TOOLS`) in
  `Dockerfile.android` to the level the error names, then `--rebuild`.
- **APK won't install over an existing app
  (`INSTALL_FAILED_UPDATE_INCOMPATIBLE`)** — the new build wasn't signed with
  the same keystore as the installed one. Make sure `docker/keystore/release.keystore`
  hasn't changed since the installed build; if it's genuinely a different key,
  the user must uninstall the old app first (this deletes app data — back up
  the wallet seed before doing this).
- **`build_android` refuses to run, asks to generate a keystore** — run
  `./docker/keystore/generate-keystore.sh` once (see `docker/keystore/README.md`).
- **Everything is broken / start from scratch** —
  `docker system prune -a && docker volume rm plm-nuget-cache`, then rerun
  the script (images and packages are re-downloaded).

---

## Linux AppImage (future)

The Linux target currently produces a single-file self-contained binary.
Once a `pupnet.conf` is added to the repository, the `build_linux` function
in `build.sh` can be extended to call PupNet Deploy inside the same
`plm-build-desktop` image to also produce an AppImage with desktop
integration (icon, menu entry).
