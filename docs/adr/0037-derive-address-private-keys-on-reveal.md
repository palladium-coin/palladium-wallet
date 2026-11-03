# 0037. Derive an address private key only when it is revealed

- **Status:** Accepted
- **Area:** Security
- **Recorded:** 2026-11-03

## Context

`AddressRow` carried the WIF of its address, computed for every listed address (ten rows before the first sync, every scanned address after it). The reveal gate (password prompt) controlled only display, not presence: for the whole session the WIF of every address sat in managed strings, which cannot be zeroed, inside objects bound to the UI. That does not leak by itself, and `SECURITY.md` already states that keys live in process memory after unlock, but it widened the exposure to a memory dump or a binding bug for no benefit.

## Decision

- `AddressRow` and `AddressInfo` hold no key. `AddressRow` keeps `HasPrivKey` (true when the account is not watch-only); `AddressInfo` carries the derivation coordinates (`IsChange`, `Index`).
- The ViewModel derives the WIF in the reveal step, after the password check (`RevealPrivKeyInternal`), into `RevealedPrivKey`, which the overlay binds to.
- `RevealedPrivKey` is cleared when the key is hidden, when the sheet changes or closes, and when the wallet closes.

## Consequences

- The exposure window of an address key shrinks from the whole session to the time it is shown.
- The WIF string still cannot be zeroed; it lives until the garbage collector reclaims it. This is documented in `SECURITY.md`.
- Watch-only wallets still never reveal a key (`HasPrivKey` is false and the derivation returns nothing).

## References

- `src/App/ViewModels/MainWindowViewModel.cs`
- `src/App/ViewModels/MainWindowViewModel.Receive.cs`
- `tests/PalladiumWallet.Tests/App/MainWindowViewModelSecurityTests.cs`
