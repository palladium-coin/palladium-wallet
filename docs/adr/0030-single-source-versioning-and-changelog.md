# 0030. Single-source versioning with a mandatory changelog entry

- **Status:** Accepted
- **Area:** Distribution
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

The App version, Android's display version and Android's `versionCode` must stay consistent, and `versionCode` must strictly increase or users cannot update in place.

## Decision

`<Version>` in `src/App/PalladiumWallet.App.csproj` is the only source. `update-version.sh` updates it, mirrors `ApplicationDisplayVersion` in the Android head, increments `ApplicationVersion` and stubs a `CHANGELOG.md` entry. The changelog entry is filled in before or with the tag; commit and tag are manual.

## Consequences

- The title bar, update checker (0023) and release builds read the same value.
- The changelog is the technical record of what shipped, not optional bookkeeping.

## References

- `update-version.sh`
- `CHANGELOG.md`
- `src/App/PalladiumWallet.App.csproj`
