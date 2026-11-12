# 0067. History columns, empty states and where a message is shown

- **Status:** Accepted
- **Area:** GUI
- **Recorded:** 2026-11-11

## Context

Going through every screen with the glass look (ADR 0066 and the layout pass before it) left a list of small defects that
were each too small for an ADR but together change how the wallet reads: the History list had no column headers and showed
the block height under a "confirmation" heading; the details of a *received* transaction listed "Transaction fee" as if it
were a cost to the person; the balance card said an outgoing amount was "not yet spendable"; a new wallet's History and
Contacts tabs were blank; a message that is not an error (such as "Settings saved") appeared in the amber box meant for
problems; errors of the Coins tab and of the server window showed only in the status bar, which is under the server
window while it is open; and a 600 px high window gave the tabs little room under the full balance card.

## Decision

- **History** has column headers on the desktop (Confirmations, Amount, Transaction ID) and shows the **number of
  confirmations** (`tip - height + 1`, or "in mempool"), not the block height; the height stays in the transaction details. The
  txid is shortened to its first and last ten characters; the full id is in the details. The block date is not shown in the
  list: it needs the block header, which the wallet cache does not keep, and the details already show it.
- **Transaction details** hide the fee row when the wallet only received (`ShowFee`); the fee rate stays, since it says how
  fast the transaction confirms. A sent transaction still shows its fee, negative.
- **Pending amounts**: incoming keeps "pending confirmation: +X — not yet spendable"; outgoing reads "outgoing, awaiting
  confirmation: -X" (money that left is not "not yet spendable").
- **Empty states**: History and Contacts say what to expect instead of showing nothing; the column header disappears with
  the rows.
- **Severity of a status**: `StatusMessage` assigned directly is a problem and feeds the wizard's inline notice;
  `SetInfoStatus` marks a confirmation ("Settings saved", "Wallet opened"), which stays in the status bar only
  (`ShowNotice`).
- **Messages next to where they come from**: `CoinsMessage` (Coins tab: a coin that cannot be selected, a failed save after
  freezing) and `ServerMessage` (server window: peer discovery result or error, certificate reset) are shown in their own
  screen. The rest keeps using the status bar, which is visible when no overlay covers it.
- **Compact balance card**: below 640 px of height the root gets the class `short` and the card has less padding and a
  smaller amount.
- **GitHub Actions are pinned to commit hashes** (the tag is kept in a comment); updating them is a manual change, no bot for now.
- **Not done, on purpose**: masking the seed and WIF entry boxes. The restore box takes 12 to 24 words and the WIF box takes
  several keys, one per line; a masked multi-line box cannot be checked before it is used (a typo in a recovery phrase is
  the common failure), a one-line mask cannot hold them, and the same words are shown in the clear when a wallet is created.
  Shoulder-surfing during entry stays a user precaution. The French contact-picker text was shortened instead of the layout
  changed. The "Wallet" menu label stays untranslated: it is the name of the product and common jargon in these languages.

## Consequences

- The History list reads like Addresses and Contacts and answers "how safe is this payment" at a glance.
- A message set with a plain `StatusMessage =` stays a "problem" by default, so a new error needs no extra code; only a
  confirmation has to opt out.
- The date of a transaction is not in the list. Showing it would mean storing the block time in `CachedTx` (a wallet file
  format change) or fetching headers for the list.

## References

- `src/App/ViewModels/MainWindowViewModel.Receive.cs` (`ApplyCache`), `.cs` (`HistoryRow`, `ShowNotice`, `SetInfoStatus`), `.Coins.cs`,
  `.Sync.cs`; `src/App/ViewModels/TransactionDetailsViewModel.cs`; `src/App/Views/MainView.axaml`, `MainView.axaml.cs`
- `tests/PalladiumWallet.Tests/App/HistoryAndMessagesTests.cs`, `RegtestGuiTests.cs` (review card against the real node)
- ADR 0065 (release workflow)
