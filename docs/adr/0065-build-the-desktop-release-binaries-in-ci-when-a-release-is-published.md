# 0065. Build the desktop release binaries in CI when a release is published

- **Status:** Accepted
- **Area:** Distribution
- **Recorded:** 2026-11-11

## Context

The release binaries were built by hand with `docker/build.sh` (toolchain pinned in the Dockerfiles, ADR 0032 covers the
test CI) and uploaded to the GitHub release afterwards. That is slow and easy to forget, and a user cannot tell which
process produced a given file. Two constraints shape any automation:

- A person must still be able to run the same build locally, to test a binary before releasing it.
- The Android APK is signed with a persistent keystore (`docker/keystore/`) that must not leave the release manager's
  machine: a leaked signing key lets someone ship updates that install over the real app.

## Decision

- **`.github/workflows/release.yml`** runs when a release is **published** (`release: types: [published]`), on
  `ubuntu-latest`, at the release's tag. It builds Windows x64, Linux x64 and Linux ARM64 by calling
  `./docker/build.sh <target>` once per target: **the same script, the same pinned image** as a local build, so the CI
  output and a local build differ only in embedded metadata (see `docker/README.md`).
- **The job refuses a tag that does not match `<Version>`** in `src/App/PalladiumWallet.App.csproj` (`v1.3.0` or `1.3.0`):
  the update check compares that version with the latest release, so a mismatch would announce a wrong update.
- **It attaches the three binaries, a `SHA256SUMS` file and a build-provenance attestation**
  (`actions/attest-build-provenance`, the repository is public) to the release, with `gh release upload --clobber`. Only
  this job has `contents: write`, `id-token: write` and `attestations: write`; the workflow default is `contents: read`.
- **Android is not built in CI.** The release manager builds and signs it locally (`./docker/build.sh android`) and
  uploads the APK to the existing release with `gh release upload`. Its checksum is added by hand (the CI file does not
  list it).
- The tag name reaches the shell only through an environment variable, never by interpolation into the script.

## Consequences

- A release has its desktop binaries a few minutes after it is published (the Docker image is built on each run), and
  anyone can compare a download with the attested checksum.
- The Windows executable is still **not Authenticode-signed**: SmartScreen warns. Signing needs a certificate and a
  secret, and is a separate decision.
- Android users can see the release (and the update notice) before the APK is attached; the release manager uploads it
  right after publishing, or keeps the release a draft until the APK is ready and publishes then (the CI run starts at
  publication either way).
- The workflow runs with write permission on the release, so it is never triggered by pull requests. The actions are
  pinned to major tags like the test workflow; pinning to commit hashes is a possible hardening.
- The release workflow has not run on GitHub yet: its shell steps were exercised locally with stand-in files and the YAML
  parsed, but the first real run is the first full test (a failed run changes nothing on the release).

## References

- `.github/workflows/release.yml`, `.github/workflows/ci.yml` (ADR 0032)
- `docker/build.sh`, `docker/Dockerfile.desktop`, `docker/README.md`, `docker/keystore/README.md`
- `update-version.sh`, `src/Core/Net/UpdateChecker.cs`
- `SECURITY.md` (Release binaries), `USERGUIDE.md` (§3.1, §3.2)
