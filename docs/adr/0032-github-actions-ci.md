# 0032. Run tests, fuzz smoke and docs checks in GitHub Actions

- **Status:** Accepted
- **Area:** Testing
- **Recorded:** 2026-11-03

## Context

The suite (unit, property-based, fuzz-corpus replay, in-process fake-server tests) is the primary verification layer for a wallet where a regression can lose funds, but it only ran on a developer's machine. Nothing prevented a failing change, a broken docs index or a Windows-only break from reaching `main`.

## Decision

`.github/workflows/ci.yml` runs on every push to `main` and on every pull request, with read-only permissions and cancellation of superseded runs:

- **test**: restore, build (Release) and `dotnet test` of `tests/PalladiumWallet.Tests` on `ubuntu-latest` and `windows-latest`, uploading the TRX and coverage results as artifacts. Only the test project and its references are built, because the Android head needs a workload and SDK that CI does not provision.
- **fuzz-smoke**: one job per parser target, running `--random 20000` mutations; any escaping exception fails the job.
- **docs**: `docs/check-docs.sh`.

The pipeline is offline: `LiveServerTests` does nothing without `PALLADIUM_TEST_SERVERS`, so no third-party indexing server is contacted. Coverage-guided fuzzing (afl++) and release builds stay manual.

## Consequences

- A change that breaks tests on either OS, a fuzz contract or the docs checks is visible before merge.
- Release, Android and Docker builds are not exercised by CI yet.
- Action versions are pinned to major tags; moving to commit SHAs would harden the supply chain further.
- Runs consume hosted-runner minutes; the fuzz matrix is the cheapest place to cut if needed.

## References

- `.github/workflows/ci.yml`
- `docs/check-docs.sh`
- `tests/PalladiumWallet.Fuzz/Program.cs`
- Related: 0025, 0026, 0027
