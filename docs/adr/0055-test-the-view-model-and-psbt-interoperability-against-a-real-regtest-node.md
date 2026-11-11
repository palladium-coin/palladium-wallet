# 0055. Test the view model and PSBT interoperability against a real regtest node

- **Status:** Accepted
- **Area:** Testing
- **Recorded:** 2026-11-10

## Context

The GUI view model and the PSBT flows were verified only against in-process fake servers and against this
wallet's own signer. Three claims therefore rested on assumptions: that the Coins, Send and broadcast-reconciliation
flows work with a real node and indexer; that a PSBT this wallet writes is valid for an independent implementation;
and that this wallet signs a PSBT written by one. The reconciliation of ADR 0047 had been checked once by hand through
a throw-away proxy that was not in the repository. The view model was also hard-wired to mainnet, so a regtest chain
could not be reached at all.

## Decision

- The view model's network follows the test seam `ProfileOverride` (`Net => ProfileOverride?.Kind ?? Mainnet`).
  The shipped app is unchanged: the seam is null there.
- `RegtestGuiTests` drive the view model against a real regtest node and plm-fulcrum: the Coins tab (freeze, the
  frozen total), a send from one chosen coin, and the broadcast reconciliation through `LyingProxy`, an in-process
  TCP proxy that forwards everything to the real server except `blockchain.transaction.broadcast`, which it can
  answer with another txid either after forwarding the transaction (`Swap`) or without ever forwarding it (`Drop`).
  The tests do nothing unless `tests/regtest/e2e.sh` provides the endpoint, so `dotnet test` stays offline. The Donate
  tab pays a fixed mainnet address and shares the Send flow's code, so it is not repeated on regtest.
- `tests/regtest/e2e.sh` also checks PSBT interoperability with Palladium Core as the external party: a PSBT built
  by a watch-only wallet is signed completely by the node's wallet (`walletprocesspsbt`) and accepted into the mempool;
  a PSBT built by the node (`createpsbt` + `utxoupdatepsbt`, witness UTXO only) is signed by this wallet's
  `sign-psbt` and accepted by the node.
- The witness clamp (ADR 0045) is tested through the view model's own witness wiring, with a second fake server as
  the only bootstrap server (`LiveConnectionTests`).

## Consequences

- The GUI flows and PSBT interoperability are checked against the real stack on every run of the script, not once by hand.
- Not covered: multisig is not implemented (the script kinds exist in the profile only), so there is nothing to
  test yet; a hardware signer or another wallet's PSBT dialect (for example with BIP32 origin fields) is not exercised.
- The script needs Docker and about 75 seconds; it is not part of CI.

## References

- `tests/PalladiumWallet.Tests/App/RegtestGuiTests.cs`, `tests/PalladiumWallet.Tests/Net/LyingProxy.cs`
- `tests/PalladiumWallet.Tests/App/LiveConnectionTests.cs`, `tests/regtest/e2e.sh`
- `src/App/ViewModels/MainWindowViewModel.cs` (`Net`, `ProfileOverride`)
- ADR 0045, ADR 0047, ADR 0049
