# 0021. In-app overlays instead of OS windows

- **Status:** Accepted
- **Area:** UI
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Secondary windows and popups are slow on WSLg and do not exist on Android's single-view root.

## Decision

Details (address, transaction), settings and help are full-screen `Border`s gated by an `IsXxxOpen` flag, with Open/Close commands, a backdrop handler and an Esc handler in `MainView`'s code-behind. Close buttons bind via `$parent[UserControl]`, not `$parent[Window]`.

## Consequences

- Instant open/close and identical behaviour on desktop and Android.
- Overlay state lives in the ViewModel and must be closed on navigation.

## References

- `src/App/Views/MainView.axaml`
- `src/App/Views/MainView.axaml.cs`
