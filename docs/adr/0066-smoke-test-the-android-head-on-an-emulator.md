# 0066. Smoke-test the Android head on an emulator

- **Status:** Accepted
- **Area:** Testing
- **Recorded:** 2026-11-11

## Context

The Android head is not built in CI (it needs a workload and an SDK that CI does not provision), and every other test runs
headless on the desktop view models. A crash at start, a layout that only breaks on a phone, or a platform behaviour that
Avalonia does not carry over could therefore ship unnoticed. Running the app by hand on an emulator (`README.md`) found two
such things in one session: the Receive tab's Copy button fell below the fold, and the **System** theme did not follow the
OS switching between light and dark while the app was running (the activity declares `UiMode` in `ConfigurationChanges`, so
it is not recreated, and Avalonia is not told).

Scripting an emulator has one trap: `adb shell input text` drops characters when it types fast (a mnemonic arrived as
"…abo"), so a script cannot trust it, and tapping fixed coordinates breaks with every layout change.

## Decision

- **`tests/android/smoke.py`** installs a debug APK, starts it, and walks the start screen, the four-step restore wizard,
  the creation of an unencrypted wallet, the Receive tab (Copy above the status bar), the Settings overlay, and the
  **System theme following the OS light/dark switch while the app runs**. It prints one line per check and exits non-zero on
  the first failure, leaving screenshots and the last UI dump in `--out`.
- **It drives the app through the accessibility tree** (`uiautomator dump`, which Avalonia exposes: texts, classes, bounds):
  buttons are tapped by their label, and a text box is read back after typing (word by word, slower on each of three
  attempts) so a dropped character is detected, not trusted.
- **It never changes a device it did not create without being told.** With no device attached it creates a throw-away AVD
  (`plm-smoke`, Android 14 x86_64 with KVM), boots it headless, and deletes it afterwards (`--keep` keeps both). On a device
  that was already there it only installs, starts and checks the process stays up; the full walk-through (it clears the app's
  data, creates a wallet and flips the system theme) needs `--reset-data`. It never uninstalls: a signature mismatch is
  reported.
- **It is run by hand** (the header of the script says how); CI keeps not building the Android head.
- **The Android head reports the OS mode itself**: `PlatformServices.IsSystemDark` (set in `MainActivity.OnCreate`, read by the
  view model for the "System" theme) and `MainActivity.OnConfigurationChanged` → `MainWindowViewModel.RefreshSystemTheme`.
  Desktop leaves the hook null, and Avalonia follows the OS there on its own.

## Consequences

- A start-up crash, a wizard or layout regression on a phone, and the theme behaviour are checked against a real Android
  runtime in about three minutes (most of it the emulator boot), without a person tapping through.
- It needs the Android SDK, an x86_64 system image, KVM and a built APK; it is not part of `dotnet test`.
- The checks read labels in English: the app starts in English on a fresh install, and a changed string needs the script
  updated with it.
- It does not measure frame rate (Avalonia draws into its own surface, which `dumpsys gfxinfo` does not see; the emulator's
  software GPU would not be representative anyway): the speed of the glass look still needs a real phone.
- Only the Android head is covered. The desktop System theme at runtime relies on Avalonia's own platform settings and was
  not exercised.

## References

- `tests/android/smoke.py`, `src/App.Android/MainActivity.cs`, `src/App/Services/PlatformServices.cs`
- `src/App/ViewModels/MainWindowViewModel.Settings.cs` (`ApplyTheme`, `RefreshSystemTheme`)
- `tests/PalladiumWallet.Tests/App/ThemeSettingTests.cs`
- ADR 0064 (the regtest scripts), ADR 0032 (what CI runs)
