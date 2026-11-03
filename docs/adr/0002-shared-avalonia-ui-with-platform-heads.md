# 0002. One shared Avalonia UI with thin platform heads

- **Status:** Accepted
- **Area:** Architecture
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

The wallet targets Windows, Linux and Android. Maintaining separate UIs would triple the surface to test and make behaviour drift between platforms.

## Decision

Write the UI once in the library `src/App` (Avalonia UI 12, .NET 10). `MainView` (a `UserControl`) is the shared root: hosted by `MainWindow` on desktop and used as the single-view root on Android. The heads `src/App.Desktop` (WinExe) and `src/App.Android` carry only the entry point and platform packages. Top-level services (file picker, clipboard) are reached through `TopLevel.GetTopLevel(this)`; platform differences are gated by `MainWindowViewModel.IsDesktop`.

## Consequences

- One code path for UI logic and one localization table.
- UI must stay mobile-friendly (no OS windows, see 0021) and avoid desktop-only APIs outside `IsDesktop` guards.
- Android needs its own workload, SDK and signing flow (0028, 0029).

## References

- `src/App/Views/MainView.axaml`
- `src/App.Desktop`
- `src/App.Android`
