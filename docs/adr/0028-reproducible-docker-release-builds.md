# 0028. Reproducible release builds inside pinned Docker images

- **Status:** Accepted
- **Area:** Distribution
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Users of a wallet must be able to trust that a shipped binary comes from the published source, and builds must not drift with whatever SDK a developer has installed.

## Decision

`docker/build.sh [windows|linux|android|all]` builds each target in a container whose toolchain (.NET SDK, JDK, Android SDK and workload) is pinned in `docker/Dockerfile.*`; artifacts land in `dist/`. The desktop single-file publish needs `IncludeNativeLibrariesForSelfExtract=true`, otherwise Avalonia's native libraries stay outside the executable and it silently fails to start.

## Consequences

- The Dockerfiles are the auditable definition of how releases are made.
- This pins the environment; bit-for-bit identical output is not guaranteed by .NET (timestamps, signing).
- The Android workload dictates the SDK API level (error XA5207 means bump `ANDROID_SDK_PLATFORM`).

## References

- `docker/build.sh`
- `docker/Dockerfile.desktop`
- `docker/Dockerfile.android`
- `docker/README.md`
