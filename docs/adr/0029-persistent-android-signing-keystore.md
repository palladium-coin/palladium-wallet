# 0029. Persistent Android release keystore kept out of git

- **Status:** Accepted
- **Area:** Distribution
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Android refuses to update an installed app signed with a different key, and the user would have to uninstall, which deletes the wallet data.

## Decision

Release APKs are signed with one persistent keystore generated once by `docker/keystore/generate-keystore.sh`. `release.keystore` is ignored by git; passwords are supplied through environment variables or prompts at each build and are never stored. `docker/build.sh android` refuses to run without the keystore.

## Consequences

- In-place updates keep working across releases.
- Losing the keystore or its passwords permanently breaks in-place updates; it must be backed up outside the repository.

## References

- `docker/keystore/generate-keystore.sh`
- `docker/keystore/README.md`
- `.gitignore`
