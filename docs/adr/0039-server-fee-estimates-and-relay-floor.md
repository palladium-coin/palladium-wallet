# 0039. Server fee estimates, relay floor and a high-fee warning

- **Status:** Accepted
- **Area:** Wallet
- **Recorded:** 2026-11-03

## Context

`EstimateFeeAsync` and `RelayFeeAsync` existed but nothing called them. The default fee rate was the constant 1 sat/vB and the only validation was "greater than zero" (and the F-01 upper bound). A rate below the node's minimum relay fee built a transaction that was rejected only at broadcast, with a generic server error, and a congested chain gave no guidance. ADR 0017 chose a single manual fee rate; this extends it without replacing the manual field.

## Decision

- After connecting, the ViewModel asks the server for `blockchain.relayfee` and `blockchain.estimatefee` with targets 2, 6 and 24 blocks (`FeePolicy.FetchAsync`, `Core/Wallet/FeePolicy.cs`). Each call is optional: an error or a malformed answer leaves that value empty.
- Values are converted from coin/kB to sat/vB (× 100,000,000 / 1,000), rounded to 2 decimals. `-1` (no estimate), zero, negative, or above `CoinAmount.MaxFeeRate` (1000 sat/vB) is discarded as implausible. An estimate is raised to the relay fee if lower.
- The Send form offers Fast, Normal and Economy buttons and a hint line with the numbers; Normal is preselected only if the user has not typed a rate. Without usable estimates the presets are hidden and the manual field keeps the 1 sat/vB default.
- A manual rate below the known relay fee is refused before building, with a message naming the minimum. Donate follows the same rules.
- The estimate is untrusted input, so the absolute fee is always shown, and the preview adds a warning when the fee exceeds 1% of the amount sent (`FeePolicy.IsHighFee`). The warning is advisory, not blocking.

## Consequences

- A rate the node would reject is caught locally with a clear message; the default follows the network instead of a constant.
- A malicious server can still suggest a high-but-plausible rate (up to 1000 sat/vB); the bound, the visible absolute fee and the 1% warning limit the damage, and the user can always type a rate.
- Presets fetched once per connection are not refreshed during a long session; reconnecting refreshes them.
- The relay-fee check relies on a value from the same server; a server that reports a high relay fee can only make the wallet more conservative, not less.

## References

- `src/Core/Wallet/FeePolicy.cs`
- `src/App/ViewModels/MainWindowViewModel.Send.cs`
- `tests/PalladiumWallet.Tests/Wallet/FeePolicyTests.cs`
- Related: 0017
