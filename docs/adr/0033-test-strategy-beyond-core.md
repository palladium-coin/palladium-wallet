# 0033. Test the UI layer and CLI headlessly and gate coverage

- **Status:** Accepted
- **Area:** Testing
- **Recorded:** 2026-11-03

## Context

`Core` was covered at about 99% of lines, but `App` (ViewModels, localization) and `Cli` had no tests at all, although they hold logic that moves funds and handles secrets: the send flow, password and lock handling, seed and private-key reveal, the wizard. Nothing stopped coverage from silently dropping.

## Decision

- **ViewModels** are tested headlessly with `Avalonia.Headless` (no window, no GPU). The helper package `Avalonia.Headless.XUnit` is not used because it requires xunit v3 and the suite is on xunit 2; `UiSession` runs each test body on a shared headless session instead. Tests are sandboxed: the data root is a temp folder, the saved server points at the in-process `FakeElectrumServer`, and two internal seams keep them offline: `MainWindowViewModel.UpdateCheck` (stubs the GitHub check) and `MainWindowViewModel.ProfileOverride` (removes the real bootstrap servers).
- **CLI** is tested end to end by invoking the real entry point in-process with a captured console, against the fake server (create, restore, info, sync, send).
- **Localization** has consistency tests: every key has all seven languages, placeholders match across languages, and every key used in XAML or C# exists.
- **Tests that touch process-wide state** (`AppPaths`, `Loc`, `Console`, the seams above) share the xUnit collection `AppPaths` so they run serially.
- **Coverage** is measured with `tests/coverage.runsettings` (test tooling excluded). CI fails if `Core` drops below 95% of lines or 88% of branches (`tests/check-coverage.py`); `App` and `Cli` are reported but not yet gated.

## Consequences

- Regressions in the wizard, reveal gating, lock handling and send preparation are caught without a display.
- Writing the tests found a real defect in `ElectrumClient` (requests hung forever after a protocol error); it is fixed and covered.
- `MainView` code-behind (window-bound) and the Android head remain untested.
- Headless tests run background timers; the test seams must stay in place for the life of the test process so that leftover ViewModels cannot reach the network.

## References

- `tests/PalladiumWallet.Tests/App/HeadlessSetup.cs`
- `tests/PalladiumWallet.Tests/App/VmTestBase.cs`
- `tests/PalladiumWallet.Tests/App/CliTests.cs`
- `tests/PalladiumWallet.Tests/App/LocalizationTests.cs`
- `tests/coverage.runsettings`
- `tests/check-coverage.py`
- Related: 0025, 0026, 0032
