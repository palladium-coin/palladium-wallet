# 0059. Create multisig wallets in the GUI

- **Status:** Accepted
- **Area:** UI
- **Recorded:** 2026-11-10

## Context

Multisig wallets could only be created from the CLI (ADR 0056); the GUI opened them. The check that matters for a
multisig wallet, comparing the wallet ID with the other cosigners (ADR 0057), is also the step a graphical flow is best
placed to make hard to skip.

## Decision

- A **Multisig wallet** button on the wizard's first step leads to two new steps, then the usual name and password step:
  1. **Keys:** the threshold M, all N cosigner keys (one per line, this wallet's included), a P2SH-P2WSH switch
     (ADR 0058), and optionally this wallet's own seed (typed, or generated with *New seed*; it is kept in the encrypted
     wallet file and can be read again under Wallet information). *Show my key* prints the key the seed gives, in the
     form the other cosigners enter. Without a seed the wallet is watch-only: it follows the funds and prepares PSBTs.
  2. **Wallet ID:** the ID the keys build is shown large, with a field for the ID the other cosigners read. A different
     ID is refused and nothing is written; an empty field is allowed but the next step says the ID is **not verified**.
- The logic is in `MainWindowViewModel.Multisig.cs` and reuses `WalletLoader.NewMultisig`, so the GUI and the CLI cannot
  disagree about what a set of keys builds. `Cosigner.FromMnemonic` is the key a seed contributes.

## Consequences

- A cosigner can set up the wallet without the command line, and is shown the ID before the file exists.
- The comparison is still the user's: skipping it is possible by design (a first cosigner has nobody's ID to type yet),
  and the warning says so.
- A seed generated here has no separate confirmation step as the single-key wizard has; the user is told it is kept in
  the wallet and shown under Wallet information.

## References

- `src/App/ViewModels/MainWindowViewModel.Multisig.cs`, `MainWindowViewModel.Wizard.cs`, `src/App/Views/MainView.axaml`, `src/Core/Crypto/Cosigner.cs`
- `tests/PalladiumWallet.Tests/App/MultisigWizardTests.cs`
- ADR 0056, ADR 0057, ADR 0058
