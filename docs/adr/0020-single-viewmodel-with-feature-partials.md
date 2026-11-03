# 0020. A single ViewModel split into partial classes by feature

- **Status:** Accepted
- **Area:** UI
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

The UI is one `MainView` with many overlays sharing wallet, sync and connection state; many small ViewModels would need heavy cross-wiring.

## Decision

Use one `MainWindowViewModel` (CommunityToolkit.Mvvm: `[ObservableProperty]`, `[RelayCommand]`) that drives `Core` directly, split into partial classes by feature (`.Send`, `.Receive`, `.Sync`, `.Settings`, `.Wizard`, `.Contacts`, `.Update`, `.WalletInfo`, `.Donate`). New feature logic goes into the matching partial or a new one.

## Consequences

- Shared state needs no messaging layer.
- The class is large; discipline on partial boundaries is what keeps it navigable.
- Heavy network work must run off the UI thread (`Task.Run`).

## References

- `src/App/ViewModels/MainWindowViewModel.cs`
- `src/App/ViewModels/MainWindowViewModel.Send.cs`
- `src/App/ViewModels/MainWindowViewModel.Sync.cs`
