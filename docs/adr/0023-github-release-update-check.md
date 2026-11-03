# 0023. Best-effort update check against GitHub releases

- **Status:** Accepted
- **Area:** Distribution
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Android builds are signed sideloads and desktop builds are single-file executables, so there is no store to notify users of fixes.

## Decision

`UpdateChecker.CheckAsync` compares the running version (single source: `<Version>` in the App csproj) with the latest GitHub release tag at startup. Any network or parse failure, or an up-to-date app, resolves to null; the UI prompts only when a newer release exists. The HTTP handler is injectable so tests never reach GitHub.

## Consequences

- Users learn about security fixes promptly; no telemetry is sent beyond the release API request.
- The check leaks to GitHub that a wallet started; this is not covered by a Tor/proxy option.
- The repository URL is hardcoded in the source and must be kept in sync if the project moves.

## References

- `src/Core/Net/UpdateChecker.cs`
- `src/App/ViewModels/MainWindowViewModel.Update.cs`
- `tests/PalladiumWallet.Tests/Net/UpdateCheckerTests.cs`
