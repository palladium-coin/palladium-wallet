# Palladium Wallet — User Guide

This guide covers **every user-facing feature** of Palladium Wallet (GUI and CLI), including
default values, validation rules, limits, and edge cases. It is written so that no behavior
has to be guessed: when the wallet enforces a rule, the rule is stated here with its exact
numbers.

Applies to version **0.9.x**. Where the GUI and the CLI differ, both are described.

---

## Table of contents

1. [What Palladium Wallet is (and is not)](#1-what-palladium-wallet-is-and-is-not)
2. [Key concepts you must understand before holding funds](#2-key-concepts)
3. [Installation and platform differences](#3-installation-and-platform-differences)
4. [First launch: the setup wizard](#4-first-launch-the-setup-wizard)
5. [Opening, closing and managing multiple wallets](#5-opening-closing-and-managing-multiple-wallets)
6. [The main screen](#6-the-main-screen)
7. [Receiving funds](#7-receiving-funds)
8. [Sending funds](#8-sending-funds)
9. [Transaction history and details](#9-transaction-history-and-details)
10. [The Addresses tab](#10-the-addresses-tab)
11. [Contacts](#11-contacts)
12. [Connection, servers and certificate pinning](#12-connection-servers-and-certificate-pinning)
13. [Settings](#13-settings)
14. [Wallet Information (xpub, seed, fingerprint)](#14-wallet-information)
15. [Backup and recovery](#15-backup-and-recovery)
16. [Security model and known limitations](#16-security-model-and-known-limitations)
17. [Command-line interface (CLI)](#17-command-line-interface-cli)
18. [Troubleshooting and error messages](#18-troubleshooting-and-error-messages)
19. [Glossary](#19-glossary)

---

## 1. What Palladium Wallet is (and is not)

Palladium Wallet is a **self-custody SPV (Simplified Payment Verification) wallet** for the
Palladium (PLM) cryptocurrency, a Bitcoin-derived UTXO chain with 2-minute blocks. It runs on
Windows, Linux and Android from the same codebase, plus a separate command-line interface.

**Self-custody** means: *you* hold the keys. There is no account, no server-side backup, no
password recovery service. If you lose your seed phrase (and your wallet file plus its
password), your funds are permanently unrecoverable. Nobody — including the developer — can
restore them.

**SPV** means the wallet does **not** download or validate the full blockchain. It connects to
an **indexing server** (ElectrumX-compatible, ports 50001 TCP / 50002 SSL) and downloads only
the data relevant to your addresses. Every *confirmed* transaction the server reports is
independently verified with a **Merkle proof** against the block header; a server that lies
about confirmed transactions is detected and the synchronization aborts. See
[section 16](#16-security-model-and-known-limitations) for exactly what the server can and
cannot do to you.

What this wallet intentionally does **not** include (as of this version):

- Lightning Network
- Hardware-wallet integration
- RBF fee bumping (transactions *signal* RBF, but there is no "bump fee" button)
- Tor/proxy support — the indexing server can observe which addresses you query
- Fiat currency conversion
- Network selection in the GUI — the **GUI operates on mainnet only**; testnet and regtest
  are available through the CLI (`--net`)

---

## 2. Key concepts

Read this section once, carefully. Every rule below is enforced by the software exactly as
written.

### 2.1 The seed phrase (BIP39 mnemonic)

- When you create a new wallet in the GUI, it generates a **12-word English** BIP39 mnemonic.
  It is shown **once**, during the wizard, and you must write it down **on paper, in order**.
- Whoever knows these words controls the funds — from any device, forever, with no further
  information needed (unless you also set a passphrase, see below).
- When *restoring*, the wallet accepts any valid BIP39 mnemonic (12 or 24 words in the GUI
  prompt; the parser also accepts 15/18/21-word mnemonics and auto-detects the wordlist
  language). The checksum is validated: a mistyped word is rejected, not silently accepted.

### 2.2 The optional passphrase ("25th word")

During creation or restore you may set a **BIP39 passphrase**. Understand precisely what it
does:

- The passphrase is combined with the seed words to derive the keys. Seed + passphrase
  produce a **completely different wallet** than the same seed without a passphrase (or with
  any other passphrase, including one differing by a single character or by case).
- There is **no validity check possible**: entering the "wrong" passphrase during a restore
  does not produce an error — it silently opens a different, empty wallet. If you restore and
  see a zero balance where you expected funds, the first thing to check is the passphrase
  (then the script type, see 2.3).
- Store the passphrase **separately** from the seed words. Losing the passphrase makes the
  funds unrecoverable even if you still have the 12 words.

### 2.3 Script type (address format)

Each wallet is created with exactly one **script type**, which determines the address format
and the derivation path. It cannot be changed later; to switch, create a new wallet and move
the funds.

| Script type | Standard | Derivation | Mainnet addresses look like |
|---|---|---|---|
| Legacy | BIP44 | `m/44'/746'/0'` | start with `P` |
| Wrapped SegWit | BIP49 | `m/49'/746'/0'` | start with `3` |
| **Native SegWit** (default, recommended) | BIP84 | `m/84'/746'/0'` | start with `plm1q` |
| Taproot | BIP86 | `m/86'/746'/0'` | start with `plm1p` |

**Restoring with the wrong script type is the same trap as the wrong passphrase**: the seed
is accepted, but different addresses are derived and the balance shows zero. When restoring,
select the same script type the wallet was created with. If unsure, try Native SegWit first
(the default), then the others.

The coin type in the derivation path is **746** on mainnet (1 on testnet/regtest).

### 2.4 Wallet types

| Type | Created via | Can sign/spend | Notes |
|---|---|---|---|
| HD (BIP39 seed) | Create new / Restore from seed | Yes | The normal case |
| HD (imported xprv) | Import extended key (xprv/yprv/zprv) | Yes | Full account key, no seed words |
| Watch-only (xpub) | Import extended key (xpub/ypub/zpub) | **No** | Sees balances/history; cannot sign. In the GUI it can *prepare* a transaction preview but not sign or broadcast it. The CLI produces an unsigned PSBT for offline signing (`sign-psbt` signs it on the machine with the seed) |
| Multisig (M-of-N) | Wizard *Multisig wallet*, or CLI `create-multisig` | Only with `M` cosigners | P2WSH or P2SH-P2WSH, BIP48 keys, addresses as `sortedmulti`. The GUI creates, opens, syncs and shows it and prepares the PSBT, but never broadcasts an incomplete transaction. See [8.8](#88-multisig-wallets). |
| Imported WIF keys | Import WIF key(s) | Yes | Fixed address list, **no HD derivation and no change chain — change always returns to the first imported address** (this links your coins together; prefer an HD wallet for privacy) |

The extended-key format is **SLIP-132**: `xpub`/`xprv` (Legacy and Taproot), `ypub`/`yprv`
(Wrapped SegWit), `zpub`/`zprv` (Native SegWit). The script type is auto-detected from the
prefix when importing.

### 2.5 The wallet file and its encryption

Everything the wallet knows is stored in **one file per wallet**:
`<data folder>/mainnet/wallets/<name>.wallet.json` (default name `default.wallet.json`).
It contains the seed (or xpub/xprv/WIF keys), the derivation settings, your **contacts**, and
a sync cache (balances, history, verified proofs).

- If you set a password, the file is encrypted with **AES-256-GCM**; the key is derived with
  **PBKDF2-HMAC-SHA512, 600,000 iterations, random 16-byte salt** (fresh salt and nonce on
  every save). Tampering with the file is detected before decryption.
- If you **decline encryption** (the wizard checkbox, or `--no-encryption` in the CLI),
  **the seed is stored in plaintext on disk**. The wizard warns you explicitly. Only do this
  on a disk you fully trust (e.g. an encrypted volume).
- There are **no password strength requirements and no attempt limits**. An empty password is
  not allowed when encryption is enabled. Choose a strong password yourself: an attacker who
  copies the file can try passwords offline at their leisure.
- There is currently **no "change password" function** in the GUI. To change the password,
  restore the wallet from its seed into a new wallet file with the new password, verify the
  new file opens and syncs, then delete the old file. (Contacts must be re-entered — see
  [section 15](#15-backup-and-recovery).)

### 2.6 Confirmations and spendability

Incoming funds are not immediately spendable. The exact rules on mainnet:

- **Unconfirmed (mempool)** transactions: shown as *"pending confirmation … not yet
  spendable"*. Never spendable, and — important — unconfirmed amounts are reported by the
  server **without cryptographic proof**, so treat them as provisional until confirmed.
- **Regular outputs**: spendable after **6 confirmations** (~12 minutes at 2-minute blocks).
- **Mining (coinbase) outputs**: spendable after **121 confirmations** (~4 hours). Until
  then they appear as *"maturing … not yet spendable"*.

The main balance shown is *confirmed minus immature*; pending and maturing amounts are shown
as separate amber lines under the balance when present.

---

## 3. Installation and platform differences

### 3.1 Desktop (Windows / Linux)

Release binaries are self-contained — no runtime to install. Run the single executable
(`PalladiumWallet.exe` on Windows, `PalladiumWallet` on Linux).

### 3.2 Android

Install the `.apk` (sideloading must be allowed). Minimum Android version: 6.0 (API 23).
The app requests the **camera** permission only if you use the QR scanner; **internet** is
required for synchronization.

> **Updating in place**: releases built with the project's official signing key update over
> the installed app. If Android refuses to install an update ("app not installed" /
> signature mismatch), the new apk was signed with a different key — you would have to
> uninstall first, **which deletes the wallet file**. Back up your seed before uninstalling,
> always.

### 3.3 What differs between desktop and Android

| Aspect | Desktop | Android |
|---|---|---|
| Data location | Chosen at first run (default or custom folder) | Fixed: the app's private sandbox (step skipped) |
| Menu bar | File / Network / Settings / Wallet / Help | No menu bar; same functions reachable from the UI |
| Close overlays | `Esc` key or click the dark backdrop | Hardware/gesture **Back** button or tap the backdrop |
| QR code **scanning** (Send) | Not available | Camera scan button in the Send form |
| CLI | Available | Not available |

Everything else — wallet formats, security, sync, features — is identical.

### 3.4 Where your data lives (desktop)

Resolution order:

1. A `palladium-data/` folder next to the executable, if present (**portable mode** — create
   the folder manually before first launch to get a fully portable install).
2. The custom folder you picked in the first-run wizard (remembered via a small pointer file
   in the default location).
3. The default: `%APPDATA%\PalladiumWallet` on Windows, `~/.palladium-wallet` on Linux.

Inside the data root: `mainnet/wallets/*.wallet.json` (your wallets),
`mainnet/server-certs.json` (pinned TLS certificates), `mainnet/servers.json` (discovered
servers), `config.json` (language, unit, last server — shared across wallets).

---

## 4. First launch: the setup wizard

### 4.1 Step: data location (desktop only, very first run)

Choose where the wallet stores its data: **"Use the default path"** (shown on screen) or
**"Choose a folder…"** (native folder picker). This step never reappears once configured.
On Android it is skipped entirely.

### 4.2 Step: start

Five options:

1. **Open existing wallet** — see [section 5](#5-opening-closing-and-managing-multiple-wallets).
2. **Create a new wallet**
3. **Restore from seed**
4. **Import xpub / xprv**
5. **Import WIF key**

### 4.3 Creating a new wallet

1. **Your seed (12 words)** — the freshly generated mnemonic is displayed. Write the words
   on paper, in order. *They are never shown in full again except through the password-gated
   "Show seed" function ([section 14](#14-wallet-information)).* Do not photograph them, do
   not store them in a cloud note.
2. **Confirm seed** — retype the 12 words, space-separated. Comparison is case-insensitive
   and tolerant of extra spaces. A mismatch shows *"The words do not match: check what you
   wrote on paper."* and lets you retry.
3. **Optional passphrase** — leave empty to skip. Re-read [section 2.2](#22-the-optional-passphrase-25th-word)
   before setting one.
4. **Script type** — default Native SegWit. If unsure, keep the default.
5. **Password step** — see 4.7.

### 4.4 Restoring from seed

1. Enter the mnemonic, words separated by spaces. Invalid words or a bad checksum produce
   *"Invalid mnemonic (wrong words or checksum): check again."*
2. Enter the **same passphrase** used originally (empty if none was used).
3. Select the **same script type** used originally.
4. Password step (4.7).

After the first synchronization the balance and full history reappear. If the balance is
zero when it shouldn't be: wrong passphrase or wrong script type
([sections 2.2–2.3](#22-the-optional-passphrase-25th-word)).

### 4.5 Importing an extended key (xpub / xprv)

Paste a SLIP-132 extended key:

- **Public key** (`xpub`/`ypub`/`zpub`) → **watch-only** wallet: monitors balances and
  history, cannot spend.
- **Private key** (`xprv`/`yprv`/`zprv`) → fully spendable HD wallet (account level, no seed
  words).

The script type is detected automatically from the key prefix and shown for confirmation
(e.g. *"NativeSegwit (watch-only)"*). An unrecognized key shows *"Extended key not
recognised for this network."* — check that it is a Palladium key of a supported format, not
a key from another coin.

### 4.6 Importing WIF private keys

Paste one or more WIF keys, **one per line**. Each key controls exactly one address; there
is no HD derivation. Select the script type (which address form the keys map to), then the
password step. Remember the change-address caveat from
[section 2.4](#24-wallet-types).

### 4.7 The password step (all flows)

- **Wallet name** (optional): letters/numbers recommended; invalid filesystem characters are
  replaced with `_`. Left blank → automatic name (`default`, then `wallet-2`, `wallet-3`, …).
  A name that already exists is rejected: *"A wallet with this name already exists."*
- **"Encrypt the wallet file with the password"** — checked by default. If you uncheck it,
  the file (including the seed) is written **in plaintext** and the UI warns you.
- **Password + confirmation** — must match and be non-empty when encryption is on. No other
  rules are imposed; strength is your responsibility.
- **Create wallet** finalizes: the file is written, locked against concurrent access, opened,
  and the first connection/sync starts automatically.

---

## 5. Opening, closing and managing multiple wallets

- You can keep **any number of wallets**: each is a separate `*.wallet.json` file in the
  wallets folder. With more than one file, "Open existing wallet" shows a chooser list; with
  exactly one, it goes straight to the password prompt.
- **Password prompt**: leave empty for an unencrypted wallet. Wrong password → *"Wrong
  password."* — retries are unlimited.
- The GUI **only lists wallets inside its own wallets folder**. To use a wallet file from
  elsewhere, copy it into `<data folder>/mainnet/wallets/` first (while the app is closed,
  or before pressing Open).
- **Single-instance lock**: a wallet file can be open in only one running instance at a
  time. A second attempt fails with *"Wallet already open in another instance of the
  application."* A stale `.lock` file left by a crash is released automatically when the
  owning process is gone.
- **Switching wallets**: menu *File → Close wallet* (returns to the wizard), then *Open
  existing wallet*. *File → New / restore wallet…* also closes the current wallet first.
- **Deleting a wallet**: there is no in-app delete. Close the app and delete the
  `.wallet.json` file manually — **only after verifying you have the seed on paper** (or,
  for imported wallets, the original keys). Deleting the file of an unbacked-up wallet
  destroys the funds' keys.

After opening, the header shows the wallet's identity, e.g.
`Mainnet · NativeSegwit · m/84'/746'/0'`, with a ` · watch-only` or ` · imported` tag when
applicable.

---

## 6. The main screen

Six tabs: **History**, **Send**, **Receive**, **Addresses**, **Contacts**, **Coins**.

- **Balance** (top): confirmed, spendable balance in your chosen unit. Below it, when
  applicable, two amber advisory lines: *pending confirmation* (mempool, unproven, not
  spendable) and *maturing* (young coinbase, not spendable — see
  [section 2.6](#26-confirmations-and-spendability)).
- **Status bar** (bottom): left, the sync status (e.g. *"Synchronized: height 123456, 42
  SPV-verified transactions. Real-time updates active."*); right, the **connection
  indicator** (*connected host:port* / *connecting…* / *not connected* / *reconnecting…*).
  **Click/tap the connection indicator to open the server settings.**
- All secondary screens (settings, details, wallet info, help) are **in-app overlays**, not
  separate windows. Close them with the ✕ button, by clicking the dark backdrop, with `Esc`
  (desktop) or Back (Android).

---

## 7. Receiving funds

The **Receive** tab always shows the **next unused receive address**, as text and as a QR
code (the QR encodes the bare address).

- **Copy** puts the address on the clipboard (*"Address copied to clipboard"*).
- Give a **fresh address to each payer**. After an address receives a payment and a sync
  runs, the tab automatically advances to the next unused address. Reusing addresses is not
  blocked, but it degrades your privacy by linking payments together.
- Incoming payments appear in History **at the next synchronization** — normally within
  seconds, because the wallet subscribes to server push notifications. A payment shows as
  *mempool* first, then with its block height once mined.
- The address format is fixed by the wallet's script type
  ([section 2.3](#23-script-type-address-format)); senders must support that format (in
  particular, very old software may not send to `plm1…` bech32 addresses).

**Gap limit — the one receiving rule you must know.** The wallet derives addresses on
demand and, when restoring from seed, scans forward until it finds **20 consecutive unused
addresses** and then stops. Consequence: if you hand out many addresses that never receive
funds, do not skip ahead by more than 20 — a payment to address #30, with #10–#29 unused,
would **not be found after a restore from seed**. Under normal use (addresses handed out
sequentially, most getting used) this never triggers. The limit (20) is stored per wallet
and is not editable in the GUI.

*Example.* A wallet whose last used address is #4 scans #5–#24 and stops. If someone pays
address **#45**, that payment is invisible after a restore from seed (and to a sync from a
cache that never saw it): the balance stays at zero although the coins are on the chain.
Nothing is lost: the funds appear once the wallet scans that far, so never hand out an address
more than 20 positions beyond the last used one.

---

## 8. Sending funds

Prerequisites: the wallet must be **connected and synchronized** (otherwise: *"Connect to
the server and synchronize before sending"* / *"Synchronize before sending"*), and the
wallet must be spendable (not watch-only) to actually broadcast.

### 8.1 The form

- **From contacts** — optional dropdown that fills the recipient address from your address
  book.
- **Recipient address** — validated when you prepare: an address of the wrong network or
  with a typo is rejected (checksums make silent typos virtually impossible).
- **Amount** — interpreted in the **currently selected display unit** (PLM by default — check
  the unit in Settings before typing!). Rejected if negative, malformed, or more precise
  than 1 satoshi (0.00000001 PLM).
- **Send all** — checkbox; disables the amount field and sends the entire spendable balance,
  **with the fee deducted from the amount** (the recipient receives balance − fee).
- **Fee (sat/vB)** — the fee rate, which you can always type by hand. **Default: 1 sat/vB.**
  Once connected, the wallet asks the server for the minimum relay fee and for estimates, and
  shows three presets — **Fast** (~2 blocks), **Normal** (~6) and **Economy** (~24) — with the
  numbers underneath; **Normal** is preselected unless you already typed a rate. An estimate
  never goes below the relay fee, and an implausible one (above 1000 sat/vB) is ignored. If the
  server gives no estimate the presets are hidden and the manual field stays at 1. A manual rate
  below the server's relay fee is refused with a message (the node would reject it anyway).
  The estimate comes from the server, which is not trusted: the preview always shows the
  **absolute fee**, and warns when it is more than 1% of the amount sent. On the Palladium
  chain, blocks are rarely full, so 1 sat/vB normally confirms in the next few blocks; raise it
  if a transaction lingers in the mempool. Must be a number greater than zero and at most 1000;
  `.` or `,` is the decimal separator (no thousands separators), regardless of the system
  language.
- **QR scan** (Android only) — camera button; reads a plain address or a
  `palladium:` payment URI (the address part is used).

### 8.2 Prepare, then confirm

Sending is a **two-step** operation:

1. **Prepare transaction** — builds and signs the transaction locally, without sending
   anything. The summary card is read from the transaction itself: the **amount and the full
   address** it pays, the change that comes back to your wallet, then the txid (truncated), the
   **exact fee**, and the virtual size, e.g. `txid 4f3a…| fee 0.00000141 PLM (141 vB)`. Check
   the address character by character: it is what will be paid. Nothing has left your wallet
   yet.

   The prepared transaction is a **snapshot**. If you change anything that went into it (the
   destination, the amount, *Send all*, the fee rate, or the coins selected in the Coins tab),
   it is discarded and the summary clears: prepare again. The confirm button only ever sends
   the transaction whose summary you are looking at. If a sync then finds that one of its
   coins is no longer spendable (spent elsewhere, back in the mempool after a reorganisation,
   or frozen), the prepared transaction is dropped with a message, and you prepare it again.
   The Donate tab works the same way.
2. **CONFIRM AND BROADCAST** — transmits the prepared transaction to the server. Success
   shows the full txid; the form clears and a re-sync starts. After broadcasting, **a
   transaction cannot be cancelled** — it is in the network's mempool and will confirm.

   In the rare case that the server answers with a txid that is not the one of your
   transaction, the wallet asks the server for the transaction itself. If the server has it,
   the send is done and the message says the server replied with a different txid (report it
   to the server's operator). If the server does not have it, or the check could not be made,
   the transaction stays **pending**: press confirm again to send the same transaction once
   more, which is harmless. The sure sign that a send went through is that it appears in your
   history after the re-sync.

### 8.3 How the transaction is built (facts you may need)

- **Coin selection is automatic** unless you choose coins yourself in the Coins tab (see
  [section 8.7](#87-coins-tab-and-coin-control)). The wallet spends from its
  spendable UTXOs and sends any **change back to its own internal change chain** — this is
  why, in transaction details, one output to an address you don't recognize as "yours" may
  still be marked as yours: it is your change.
- All transactions **signal RBF** (BIP125) and use transaction version 2. The wallet does
  not offer fee bumping; if you underpay the fee, wait — with RBF signaled, other wallets
  could in principle replace it, but this wallet has no UI for that.
- **Dust change is absorbed into the fee**: if the change output would be uneconomically
  small, it is added to the fee instead of creating a dust output. The fee shown in the
  summary already accounts for this — always check the summary fee before confirming.
- A prepared transaction is fully verified against standardness policy before it can be
  confirmed; a policy failure surfaces as *"Invalid transaction: …"* instead of a broadcast.

### 8.4 Why "insufficient funds" can appear despite a visible balance

The error message itemizes the reasons. Funds may be temporarily unspendable because they
are:

- **in the mempool** (unconfirmed — never spendable),
- **recently confirmed** (fewer than 6 confirmations),
- **immature coinbase** (mining rewards younger than 121 blocks).

Wait for confirmations and retry. Also remember that the fee itself must fit: sending your
exact full balance fails unless you use **Send all**.

### 8.5 Watch-only wallets and offline signing

A watch-only (xpub) wallet in the **GUI** can prepare a transaction to preview the fee and
size — the summary is tagged *"unsigned (watch-only)"* — but the confirm button stays
disabled: **the GUI cannot export the unsigned transaction**. To actually use the
watch-only + offline-signing workflow, use the **CLI**, whose `send` command prints an
**unsigned PSBT (base64)** for a watch-only wallet
([section 17](#17-command-line-interface-cli)). Sign that PSBT on the offline machine with
software of your choice, then broadcast the finalized transaction.

The CLI can also be the offline signer: on the machine that holds the seed, run
`sign-psbt <psbt-base64> --file seed-wallet.json` (see [section 17](#17-command-line-interface-cli)).
It prints the inputs, outputs (your own change marked) and fee, signs, and, when every input
is signed, prints the final transaction; with `--broadcast --server …` it also transmits it.
For an external or hardware signer to recognise its keys in the PSBT, import the xpub with
its **master fingerprint** and **account path** (the import step of the GUI and
`restore-xpub --fingerprint … --path …` both take them). Note that for legacy and
P2SH-wrapped inputs the txid shown before signing is not the final one; for native segwit it is.

### 8.6 The Donate tab (Help → Donate)

The Help overlay includes a donation form (developer address
`plm1qdq3gu2zvg9lyr8gxd6yln4wavc5tlp8prmvfay`) using the same prepare/confirm flow and the
fee rate from the Send tab (an invalid rate is reported as an error). Entirely optional.

### 8.7 Coins tab and coin control

The **Coins** tab lists every coin (UTXO) of the wallet with its amount, its outpoint
(`txid:vout`), its address and its status.

- **Freeze / Unfreeze.** A frozen coin is never spent: not by automatic selection, and not by
  coin control. Its value is also left out of the balance. The freeze is saved in the wallet
  file, so it survives restarts and syncs. A coin you froze and forgot keeps its value out of
  the balance until you unfreeze it; so that it cannot go unnoticed, the total of the frozen
  coins is shown under the balance ("Frozen coins: …").
- **Use in next send.** Select spendable coins to restrict the next send (or donation) to them.
  A summary appears in the Send tab (desktop and phone layout), and *Clear selection* removes it. A successful send
  clears the selection. The selection is not saved: it is gone when you restart the app.
- If a selected coin stops being spendable (for example, it becomes unconfirmed after a
  chain reorganisation), the send is blocked with a message until you deselect that coin.

### 8.8 Multisig wallets

An **M-of-N multisig** wallet needs `M` signatures out of `N` cosigners to spend. No single device (or
person) can move the funds alone, and losing up to `N - M` keys does not lose them. This wallet supports
**native SegWit (P2WSH)** multisig with keys derived as BIP48 (`m/48'/746'/0'/2'`), or the older **P2SH-P2WSH** form
(`…/1'`, for signers that cannot read bech32 addresses; pass `--wrapped`, or tick the box in the GUI), and addresses built the way
Bitcoin Core's `wsh(sortedmulti(M, …))` and Sparrow do (keys sorted, BIP67), so **the order in which you enter the
cosigners does not matter** and the addresses can be checked against any of them.

**Setting it up in the GUI.** On the first screen choose *Multisig wallet*. Enter the threshold `M`, paste all `N`
keys (yours too, one per line), and either type your own seed or press *New seed* (it is kept in the encrypted
wallet file; read it again under Wallet information). *Show my key* prints the key to hand to the others; tick
*P2SH-P2WSH* first if the wallet is the wrapped type, since the key differs. Next comes the **wallet ID**, shown
large: type the ID the other cosigners read (see step 3 below). A different one is refused and nothing is
saved; leaving it empty creates the wallet but warns that it is **not verified**. Without a seed the wallet
only follows the funds and prepares PSBTs.

**Setting it up (command line).** Every cosigner, on its own device and from its own seed:

1. `cosigner-key "<its mnemonic>"` prints its key, `[73c5da0a/48'/746'/0'/2']Zpub…`: the master fingerprint, the path
   and the public key. It is public; send it to the others by any channel, **then check it out of band** (read the
   fingerprint aloud): a key swapped in transit would put a stranger among the cosigners.
2. With all `N` keys, each cosigner runs
   `create-multisig --threshold M --cosigner KEY1 --cosigner KEY2 … --mnemonic "<its mnemonic>"`.
   The same keys give the same wallet on every device, and the command prints its **wallet ID** (8 characters,
   the descriptor checksum Bitcoin Core also computes): it covers every key and the threshold, whatever order they
   were typed in, and changes if a single key was altered.
3. **Check the wallet ID.** The wallet cannot tell a genuine cosigner key from one swapped on its way, so the
   cosigners compare the ID over a channel the attacker does not control (in person, a call, a different messenger
   from the one that carried the keys). Easiest: the first cosigner creates the wallet and reads the ID to the others,
   who create theirs with `--expect-id ID`: the wallet is **not written** if the ID differs, and the command says
   the keys are not the ones the other cosigner saw. Without `--expect-id` the command says `NOT VERIFIED`. The ID
   is also in `info` and, in the GUI, in the wallet information. Also compare the first receive address if you
   like, before receiving anything. A file made without `--mnemonic` is a watch-only copy that can follow the funds
   and build a PSBT but sign nothing.

**Spending.** (A multisig wallet signs only inputs it recognises as spending its own addresses, and
refuses a PSBT whose witness script is not its own.)

1. One cosigner runs `sync`, then `send --to ADDRESS --amount X`: the output is a PSBT already carrying that
   cosigner's signature (and, in the PSBT, the key origins and the change output's script, so the other signers can
   check where the change goes).
2. Each other cosigner, from the PSBT text and its own file: `sign-psbt <psbt> --file its.json`. It lists the
   inputs, the outputs (its own change is marked) and the fee before the signed PSBT: **read them: you are signing
   for the others too**. When `M` signatures are present it also prints the final transaction.
3. `combine-psbt <psbt-1> <psbt-2> …` merges the cosigners' copies (they must be of the same transaction),
   prints the final transaction when there are enough signatures, and `--broadcast --server host:port` sends it.
   `sign-psbt … --broadcast` does the same for the last cosigner.

The GUI shows the same wallet (balance, history, addresses, Coins tab) and its Send tab prepares the PSBT to pass on;
it never offers to broadcast a transaction that is not complete.

**In the GUI.** The Send tab has a **Sign a PSBT…** button: paste the PSBT (base64 or hex) or *Open file…* (a binary
`.psbt` too), press *Sign*, and read what it shows (inputs, outputs with your own change marked, the fee) before you pass
it on. The result can be copied or saved as a `.psbt` file; when the signatures are enough, *Broadcast* sends it. The wallet
information screen of a multisig wallet shows its descriptors and saves the configuration file for a signer, and the creation
step can load such a file, takes an optional seed passphrase and an account number, and shows your own key as a QR code.

**Hardware signers and other wallets.** `sign-psbt` and `combine-psbt` take a `.psbt` file (the binary file a signer
reads from and writes to an SD card, or one Sparrow saves), base64 or hex text, or `-`; add `--psbt-out FILE` to
`send`, `sign-psbt` or `combine-psbt` to also write the binary file. `export-multisig --out FILE` writes the
configuration text a signer registers a multisig wallet from (Coldcard, Sparrow), and `export-multisig --format
descriptor` prints the descriptors with their checksums; `create-multisig --config FILE` builds the wallet from such a
file, with your own `--mnemonic`. **These formats are implemented to what the tools document, but no real signer,
Sparrow or Electrum has been tried against them**: only Palladium Core has. Check the first transaction with a small
amount, and compare the wallet ID or first address shown by the device.

---

## 9. Transaction history and details

The **History** tab lists all wallet transactions, newest first; unconfirmed ones are
labeled **mempool** and sorted on top, confirmed ones show their **block height**. The
amount is the transaction's net effect on your wallet (`+` incoming).

**Double-click** (desktop) or **tap** (Android) a row to open the full details. This
requires a live server connection (details are fetched on demand): otherwise *"Connect to
the server to view transaction details."*

The details overlay shows:

- **Overview** — status (*"N confirmations (block H)"* or *"0 confirmations · in
  mempool"*), local date/time, counterparty addresses, and the signed net amount. Mining
  rewards show *"Newly generated (mining)"* as the sender.
- **Amounts & fees** — the absolute fee, your net amount, and the fee rate in sat/vB.
- **Technical details** — full transaction ID, total size, virtual size, **Replaceable
  (RBF)** yes/no, version, locktime.
- **Inputs** — each spent outpoint with its address and amount; your own inputs are
  highlighted. Coinbase inputs display the miner's **scriptSig message** when it is readable
  text.
- **Outputs** — each output with index, address and amount; your own outputs (including
  change) are highlighted. **OP_RETURN** outputs display their decoded text message when the
  payload is valid readable text; non-standard outputs show their script type.

Notes and limits:

- Confirmation counts advance automatically as blocks arrive (real-time header
  subscription).
- Every **confirmed** transaction in your history has been verified with a Merkle proof
  during sync. **Unconfirmed** entries are the server's word only — do not treat a mempool
  payment as received until it confirms.
- There are no per-transaction labels and no block-explorer links in this version. The
  public explorer is at `https://explorer.palladium-coin.com/` — paste the txid there
  manually if needed.

---

## 10. The Addresses tab

A complete table of every derived address: **type** (receive/change), **index**,
**address**, **balance**, and **transaction count**. Before the first sync it shows the
first 10 receive addresses with no data.

Tap (or right-click) a row to open the **address details** overlay:

- the full address and its **derivation path** (e.g. `m/84'/746'/0'/0/3`),
- the **public key** (hex),
- the **private key (WIF)**, hidden behind a **"Show"** button. For an encrypted wallet,
  revealing it requires re-entering the wallet password (*"Enter the wallet password to
  reveal the private key."*); for an unencrypted wallet it is shown directly. **Never
  reveal a private key with anyone watching your screen** — one key controls that one
  address's funds. Watch-only wallets have no private keys to show.

`change` addresses are where your own transactions send their change
([section 8.3](#83-how-the-transaction-is-built-facts-you-may-need)) — seeing balance on
them is normal.

---

## 11. Contacts

A simple per-wallet address book, on its own tab:

- **Add**: name + address, both required. The address is **validated when saving** against
  the wallet's network: an invalid or wrong-network address, or one already in the list, is
  refused with a message. Spaces and line breaks in a pasted address are removed. Contacts
  saved by an older version without validation are still shown; the send form validates the
  destination again when you use them.
- **Remove selected**: deletes the highlighted contact. There is no edit function — remove
  and re-add.
- Contacts are used by the **From contacts** dropdown on the Send tab.
- Contacts are stored **inside the wallet file**. They travel with a file backup, but are
  **not** recoverable from the seed phrase ([section 15](#15-backup-and-recovery)).

---

## 12. Connection, servers and certificate pinning

### 12.1 Server settings

Open by clicking the **connection indicator** in the status bar (or Settings → *Indexing
server…*, or menu *Network* on desktop). Fields:

- **Host** and **Port** — defaults: port **50001** (plain TCP) or **50002** (SSL). Editing
  one of the known ports auto-toggles the SSL switch to match, and toggling SSL swaps the
  port. Note these are the *indexing server* ports — not the Palladium node's P2P port
  (2333), which this wallet never uses.
- **Use SSL** — default **on**. Strongly recommended: without SSL, traffic (including which
  addresses you query) is readable on the wire.
- **Known servers** list — click an entry to select it. The wallet ships with four bootstrap
  mainnet servers and remembers any servers learned via discovery.
- **Discover servers (peers)** — asks the connected server for its known peers and adds new
  ones to the list (hosts that are not public — `localhost`, private networks — are skipped).
  A discovered server is never used automatically: pick it yourself if you want it.

Connection behavior: the wallet tries your selected server first, then walks the rest of
the shipped (bootstrap) servers until one answers. The last working server is remembered and reused at next
launch. A 20-second keep-alive ping detects drops and reconnects automatically;
server push notifications trigger instant re-syncs when a new block or a relevant
transaction appears (*"Real-time updates active"*).

### 12.2 TLS certificate pinning (TOFU) — read before "fixing" a certificate error

SSL connections use **Trust On First Use**: the first time you connect to a server, its
certificate's fingerprint is saved (in `server-certs.json`). Every later connection must
present the **same** certificate. If it doesn't, the connection is refused with a message
stating that the TLS certificate of that server **has changed**.

This is a security feature, not a bug. A changed certificate means one of two things:

1. **Benign**: the server operator renewed/rotated the certificate (common, e.g. Let's
   Encrypt renewals).
2. **Hostile**: someone is intercepting your connection (man-in-the-middle).

You cannot distinguish the two from the wallet alone. If the change is expected or you can
verify it out-of-band, clear the pins: menu **Network → Reset SSL certificates** (desktop),
the **Reset certs** button in server settings, or `reset-certs` in the CLI. This deletes
**all** pinned certificates for the network; the next connection re-pins whatever it sees.
If you have any reason to suspect interception, switch networks (e.g. off a public Wi-Fi)
or servers instead of resetting.

### 12.3 What synchronization actually does

Each sync: gets the chain tip → scans your receive and change address chains (gap limit
20) → downloads your transactions → **verifies a Merkle proof for every confirmed
transaction** against its block header → rebuilds your UTXO set and balances locally.
On mainnet, each block header used for a proof is also chained up (via the previous-block
hashes) to the nearest built-in checkpoint above it, which must match exactly, so a server
cannot substitute a fabricated header for a real one. The blocks mined after the newest
checkpoint (shipped with each release) cannot be anchored this way: for those only the Merkle
proof is checked, which is why updating the wallet regularly matters. Such transactions carry an
orange **"not anchored"** label in the History tab and in the transaction detail (hover it for
the explanation); it disappears once you update to a release whose newest checkpoint covers
that block. The label is informational: the transaction is still Merkle-verified, and its block header is also compared with a second built-in server when one is reachable (if they disagree about a block that is a few blocks deep the sync stops with a "server is not trustworthy" error; for a very recent block the transaction just stays "verifying" until the next sync). Verified data is cached in the wallet file, so later syncs only fetch what
is new — even after a restart. If a proof or the checkpoint chain does not check out, the
sync **aborts** with an explicit "server is not trustworthy" error rather than showing you
unverified data; switch servers.

If the server is overloaded (busy responses), the wallet retries automatically up to 8
times with increasing back-off — a large wallet's first sync may take a little while, but it
resumes from the cache instead of restarting.

**First sync can take noticeably longer than later ones — this is expected.** Each confirmed
transaction requires its own Merkle-proof round trip to the indexing server
(`blockchain.transaction.get_merkle`, one request per transaction — the Electrum-style
protocol has no batched form of this call), plus, on mainnet, chaining the covering block
header up to the nearest hardcoded checkpoint above it. Sync time therefore scales with the number
of confirmed transactions in the wallet's history, not with wall-clock time since creation.
On **Android**, the first sync is typically slower still than on desktop for the same
wallet: mobile networks add higher round-trip latency and lower sustained throughput than a
desktop's wired/Wi-Fi connection, and every proof round trip pays that latency individually.
Every subsequent sync is fast: verified proofs, raw transaction bytes, downloaded block
headers, and the checkpoint hash-chain anchoring state are all persisted into the wallet
file's cache, so a resumed or later sync only fetches and verifies what changed since the
last one — even across an app restart. After updating from a version older than the
checkpoint-direction fix (ADR 0040), the first sync re-verifies every cached proof once
(the cached headers and transactions are reused).

**Do not use this wallet as a mining payout address.** Pool or solo mining payouts typically
arrive as many small, frequent transactions, and — because of the per-transaction Merkle
proof cost described above — sync time grows with transaction count, not balance. A wallet
whose history has accumulated **over 5,000 transactions** has been observed taking on the
order of a couple of minutes to fully synchronize even on a stable connection, with slower
networks (see the Android note above) pushing that further. If you mine, pay out to a wallet
purpose-built for high transaction volume (or one that lets you consolidate UTXOs
aggressively), and only move funds into Palladium Wallet in batches.

---

## 13. Settings

The Settings overlay (gear icon / menu *Settings*) contains exactly two preferences, plus
the door to the server settings:

- **Language** — Italiano, English, Español, Français, Português, Deutsch. Applies
  immediately. Default: English.
- **Amount unit** — how amounts are displayed *and interpreted when you type them*:

  | Unit | In satoshi | Decimals shown |
  |---|---|---|
  | PLM (default) | 100,000,000 | 8 |
  | mPLM | 100,000 | 5 |
  | µPLM | 100 | 2 |
  | sat | 1 | 0 |

  ⚠ The Send form's amount field uses this unit. If you switch to `sat` and forget, typing
  `100` means 100 satoshi, not 100 PLM. Double-check the unit before sending.

Settings are saved globally (per data folder, shared by all wallets), not per wallet.
There is no theme selector (the app uses its built-in dark theme) and no fiat display.

---

## 14. Wallet Information

Menu **Wallet → Wallet Information** (desktop) or the equivalent button. Shows:

- **File name**, **network**, **wallet type** (*HD (BIP39 seed)* / *HD (imported xprv)* /
  *Imported WIF key* / *Watch-only (xpub)*), **script type**, **derivation path**.
- **Extended public key (xpub)** — displayed as selectable text. This is how you *export*
  the xpub: select and copy it. Give the xpub to another device/app to create a
  **watch-only** copy of this wallet — it reveals your entire address history and balance to
  whoever holds it (privacy risk), but can never spend.
- **Master fingerprint** — 4-byte identifier of the master key, used in PSBT workflows.
- **BIP39 passphrase** — shows only whether one is *set*; the passphrase itself is never
  displayed anywhere.
- **Show seed** — reveals the mnemonic, after re-entering the wallet password (for
  encrypted wallets). Use it to re-verify your paper backup. The on-screen warning is
  literal: *never share these words; whoever holds them controls the funds.* Watch-only and
  imported wallets have no seed to show.

---

## 15. Backup and recovery

### 15.1 What to back up

There are two complementary backups; know exactly what each one restores:

| Backup | Restores | Does NOT restore |
|---|---|---|
| **Seed words on paper** (+ passphrase if set, + script type) | All keys, addresses, balance and on-chain history, on any device, forever | Contacts; wallet name; settings |
| **The wallet file** (+ its password) | Everything, including contacts | — |

Recommended: **always** have the paper seed (it survives disk failure, device loss, and
software obsolescence); *additionally* copy the `.wallet.json` file if you care about your
contacts list. An encrypted wallet file is safe to store on ordinary media — but it is only
as strong as its password, and it is useless without it.

For a **watch-only** wallet, the file only restores the watching capability; the actual
keys live wherever you keep the corresponding seed/xprv — back *that* up.

For an **imported WIF** wallet, back up the WIF keys themselves; they are not derivable from
anything else.

### 15.2 Restoring, step by step

1. Install the wallet on the new device.
2. Wizard → **Restore from seed** → enter the words → enter the **same passphrase** (or
   leave empty if none) → choose the **same script type** → set a (new) password.
3. Let it synchronize. Balance and history are rebuilt from the chain.

If the balance is zero: wrong passphrase, or wrong script type, or (rare) funds beyond the
gap limit ([section 7](#7-receiving-funds)). Nothing is lost — the coins are still on the
chain; you are simply looking at the wrong derived wallet. Retry with the right parameters.

Restoring from a **file copy** instead: place the `.wallet.json` into
`<data folder>/mainnet/wallets/` and open it with its password.

### 15.3 Unrecoverable situations — be aware

- Seed lost **and** wallet file (or its password) lost → funds gone. No exceptions.
- Passphrase forgotten → funds gone, even with the 12 words in hand.
- Encrypted file's password forgotten, no seed backup → funds gone (the encryption has no
  backdoor).

---

## 16. Security model and known limitations

A condensed version of the project's published threat model (`SECURITY.md`).

**The wallet protects you against:**

- Theft of the wallet file at rest (AES-256-GCM + PBKDF2, [section 2.5](#25-the-wallet-file-and-its-encryption)) —
  provided you set a good password.
- A **lying indexing server**: it cannot fabricate a confirmed transaction or forge a
  payment to a wrong address, because every confirmed transaction must carry a valid Merkle
  proof, which the wallet checks itself.
- **Silent TLS interception after first contact** (certificate pinning,
  [section 12.2](#122-tls-certificate-pinning-tofu--read-before-fixing-a-certificate-error)).

**The server can still (semi-trusted component):**

- lie about **unconfirmed** transactions (which is why they are marked not-spendable and
  should not be trusted until confirmed);
- **withhold** information — hide transactions, delay new blocks, refuse to broadcast;
- **observe** which addresses belong to you (no Tor/proxy support in this version; using
  your own indexing server is the strongest mitigation).

**The wallet cannot protect you against:**

- malware on your device (anything that can read process memory can take the keys once the
  wallet is unlocked);
- an attacker holding both your wallet file **and** its password (or your seed);
- yourself: there are no confirmation "cool-downs", no address whitelists, and broadcast is
  irreversible.

**Honest limitations of this version** (also listed in [section 1](#1-what-palladium-wallet-is-and-is-not)):
single server connection (no pooling), no fee estimation, no RBF bumping,
no hardware wallets, no Tor, GUI mainnet-only, and PSBT export and signing only via the CLI.

---

## 17. Command-line interface (CLI)

The CLI (`src/Cli`) runs on the same core as the GUI: same wallet files, same validation,
same security. It is desktop-only and its console output is in English. Run it
as `dotnet run --project src/Cli -- <command>` from the repository (or the published `Cli`
binary); with no arguments it prints usage.

**Shared conventions.** Default wallet file:
`~/.palladium-wallet/<network>/wallets/default.wallet.json` — override with `--file PATH`.
Default network mainnet — override with `--net testnet|regtest` (the CLI is the only way to
use test networks).

**Wallet password.** `create`/`restore*` ask for a password and encrypt the file; encrypted
wallets need it for `info`/`sync`/`send`. Supply it, in order of preference, with:
`--password-env NAME` (read from an environment variable), `--password-stdin` (first line of
stdin), or nothing — in a terminal you are prompted (no echo, with a confirmation when
creating); with no terminal the command fails instead of hanging. `--password P` still works
but is unsafe (it stays in the shell history and the process list) and prints a warning.
Only **one** of the three options may be given. A new wallet is stored **unencrypted only
with the explicit `--no-encryption`** (a warning is printed); forgetting the password no
longer produces a plaintext wallet. Examples:
`printf '%s\n' "$PW" | dotnet run --project src/Cli -- restore "<words>" --password-stdin`,
`PLM_PW=… dotnet run --project src/Cli -- info --password-env PLM_PW`.
`--kind segwit|wrapped|legacy|taproot` selects the script type (default Native SegWit; `taproot`
is BIP86, key-path only). Any other value is an error, never a silent default. Errors print
to stderr and set exit code 1.

### Wallet commands

```
newseed  [--words 24]
```
Prints a fresh mnemonic (12 words unless `--words 24`) and writes nothing to disk.

```
create   [--words 24] [--kind K] [--net N] [--passphrase W] [--no-encryption] [--file PATH] [--path m/...]
```
Generates a mnemonic, prints it once, saves the wallet, prints the file path and the first
receive address. `--path` overrides the derivation path (advanced; you must remember it to
restore).

```
restore  "<mnemonic>" [same options as create]
```
Restores from an existing mnemonic (quoted, words space-separated).

```
restore-wif  <wif1,wif2,...|-> [--kind K] [--net N] [--no-encryption] [--file PATH]
```
Imports one or more **WIF private keys** as a spendable wallet: no seed, nothing is derived,
so only those keys' addresses are tracked, and the same key twice is stored once. `-` reads
the keys from standard input (one per line or comma separated), which keeps them out of the
process list and the shell history. The script type (`--kind`) decides which address each key
yields, and must match the type the funds were sent to. A key of another network is refused.
The keys are stored in the wallet file, so give it a password (the default) or
`--no-encryption` on purpose.

```
restore-xpub <slip132-key> [--net N] [--no-encryption] [--file PATH]
             [--fingerprint HEX8 --path m/...]
```
Creates a **watch-only** wallet from an xpub/ypub/zpub; the script type is inferred from
the key prefix. To have the PSBTs it exports recognised by an external or hardware signer,
pass the **master key fingerprint** (8 hex digits) and the **account path** the key came
from (for example `--fingerprint 73c5da0a --path "m/84'/746'/0'"`). Without a fingerprint
the PSBT carries no BIP32 origin information.

```
info     [--net N] [--file PATH] [--addresses]
```
Prints the wallet's identity and, if it has ever synced, the balance breakdown (spendable /
maturing / pending), tip height, transaction count and next receive address.
`--addresses` adds the per-address list with balances.

```
addresses "<mnemonic>" [--kind K] [--net N] [--count N] [--passphrase W] [--path m/...]
```
Stateless tool: derives and prints receive (default 5) and change addresses from a mnemonic
without creating any file. Useful to check "which script type was this seed used with"
before restoring.

### Network commands

```
sync     [--server host[:port]] [--ssl] [--net N] [--file PATH]
```
Connects (default: the first known server; port defaults to 50001, or 50002 with `--ssl`),
synchronizes with full Merkle verification, persists the cache into the wallet file, and
prints balance and history. Transactions the server reported but that are unconfirmed are
marked as unverified.

```
send     --to ADDRESS (--amount X | --all) [--feerate R] [--broadcast]
         [--server ...] [--ssl] [--net N] [--file PATH]
```
Synchronizes first (a spend must start from the chain's current state, never from an older cache),
then builds the transaction. `--amount` is
in **PLM**; `--all` sends everything minus the fee. `--feerate` is in sat/vB, **default 1**.
Behavior by wallet type and flags:

- **Watch-only** wallet → prints the **unsigned PSBT in base64** and stops. This is the
  air-gapped workflow: carry the PSBT to the offline signer, sign it there, broadcast the
  final transaction by any means.
- Spendable wallet, **without `--broadcast`** → signs and prints the raw transaction hex,
  explicitly marked as *not transmitted*. Nothing is sent — a dry run you can inspect or
  broadcast elsewhere.
- Spendable wallet, **with `--broadcast`** → signs, transmits, prints the txid. Irreversible
  from this point.

```
utxos     [--net N] [--file PATH]
freeze    <txid>:<vout> [--net N] [--file PATH]
unfreeze  <txid>:<vout> [--net N] [--file PATH]
```
Work offline on the coins of the last `sync` (modelled on Bitcoin Core's `listunspent` and
`lockunspent`). `utxos` lists each coin with its confirmations and state (spendable, frozen,
unconfirmed, awaiting SPV proof) and the frozen total. A **frozen** coin is kept out of every
spend, automatic or manual, and stays frozen across later syncs. `freeze` and `unfreeze` take a
coin as printed by `utxos`.

```
cosigner-key    "<mnemonic>" [--wrapped] [--account N] [--passphrase W] [--net N]
create-multisig (--threshold M --cosigner KEY … | --config FILE) [--wrapped] [--mnemonic "<words>"] [--passphrase W] [--expect-id ID] [--net N] [--file PATH]
export-multisig [--format descriptor] [--name N] [--out FILE] [--net N] [--file PATH]
combine-psbt    <base64|hex|file.psbt|-> <psbt> … [--psbt-out FILE] [--broadcast --server ...] [--ssl] [--net N]
```
Multisig (see [8.8](#88-multisig-wallets)). `cosigner-key` prints the key to hand to the other cosigners.
`create-multisig` takes **all `N` keys** (this wallet's included) and, with `--mnemonic`, the seed of one of them,
found by its fingerprint; `--expect-id` refuses to write the wallet unless its ID is the given one; a seed that matches no key, the same key twice, `M` outside `1..N` or fewer than two keys are
refused. `combine-psbt` merges copies of one PSBT (`-` reads them from stdin, one per line).

```
sign-psbt  <base64|-> [--scan N] [--broadcast --server ...] [--ssl] [--net N] [--file PATH]
```
Signs a PSBT with this wallet's keys. `-` reads the PSBT from standard input. It refuses a
watch-only wallet, any input that asks for a sighash other than ALL, and a previous
transaction that does not match its outpoint. It prints inputs, outputs and fee, then the
signed PSBT; if every input is signed it also prints the txid and the final transaction, and
`--broadcast` sends it. `--scan` is how many receive and change addresses to try when matching
keys to inputs (default: enough for the wallet's used addresses plus the gap limit, at least 100).

```
servers  [--discover] [--server ...] [--ssl] [--net N]
```
Lists known servers; with `--discover`, connects and asks the server for its peers, adding
new ones.

```
reset-certs [--net N]
```
Deletes all pinned TLS certificates for the network (the CLI counterpart of *Network →
Reset SSL certificates* — see
[section 12.2](#122-tls-certificate-pinning-tofu--read-before-fixing-a-certificate-error)).

---

## 18. Troubleshooting and error messages

| Symptom / message | Meaning | What to do |
|---|---|---|
| *Wrong password.* | The password does not decrypt the file (or the file was tampered with — the two are indistinguishable by design). | Retry; check keyboard layout/caps lock. If truly lost, restore from seed ([15.2](#152-restoring-step-by-step)). |
| *Wallet already open in another instance of the application.* | Another running process holds this wallet's lock. | Close the other instance (GUI or CLI). |
| *The TLS certificate of host:port has changed…* | Certificate pin mismatch — renewal or interception. | Read [12.2](#122-tls-certificate-pinning-tofu--read-before-fixing-a-certificate-error) **before** resetting certificates. |
| History shows an orange *"not anchored"* next to a transaction | The block is newer than the newest checkpoint built into this version, so its header cannot be chained to a hardcoded hash; only the Merkle proof backs it. | Normal for the most recent blocks. Update the wallet to a newer release to anchor them; for large amounts, wait until it is no longer flagged. |
| Sync aborts, "server is not trustworthy" | A Merkle proof failed, or (mainnet) a block header did not chain up to a known checkpoint. | Switch to another server. Your local data is intact; the wallet refused the bad data. |
| *The server host:port is not on the Palladium chain (different genesis or checkpoint)…* | The server answered, but serves another chain (or a fork) than the wallet expects. Nothing was synced from it. | Pick another server; the wallet moves on to the next known one on its own. See [12.3](#123-what-synchronization-actually-does). |
| *Insufficient funds* with a non-zero balance | Funds are unconfirmed, under 6 confirmations, or immature coinbase; or the fee doesn't fit. | See [8.4](#84-why-insufficient-funds-can-appear-despite-a-visible-balance). Wait, or use Send all. |
| Restored wallet shows zero balance | Wrong passphrase, wrong script type, or funds beyond the gap limit. | See [15.2](#152-restoring-step-by-step) — the coins are not lost. |
| *Invalid mnemonic (wrong words or checksum)* | A word is misspelled, not on the BIP39 list, or the checksum fails. | Compare against your paper backup, word by word. |
| *Extended key not recognised for this network.* | The pasted xpub/xprv is not a SLIP-132 key of this network. | Check the key's prefix and the source wallet's export format. |
| *A wallet with this name already exists.* | Filename collision in the wallets folder. | Pick another name, or leave blank for an automatic one. |
| Payment sent to me doesn't appear | Not yet synced/connected, or sender hasn't broadcast. | Check the connection indicator; mempool entries appear within seconds of broadcast when connected. |
| Update prompt at startup (*"Update available"*) | A newer GitHub release exists (checked once at startup, silently skipped offline). | *Download* opens the release page; *Dismiss* continues. Never enter your seed into anything but the wallet itself. |
| Android: update apk refuses to install | Signature mismatch between builds. | Back up the seed **before** uninstalling; see [3.2](#32-android). |
| First sync is slow / server busy errors | Server throttling (automatic retry, up to 8 attempts) and/or a large transaction history — sync time scales with transaction count, not balance, worse on mobile. | Wait; progress is cached, so restarting resumes rather than repeats. See [12.3](#123-what-synchronization-actually-does). Do not use this wallet for mining payouts (many small transactions). |

---

## 19. Glossary

- **SPV** — Simplified Payment Verification: validating that transactions are included in
  blocks via Merkle proofs, without running a full node.
- **Seed / mnemonic** — the 12 (or 24) BIP39 words that deterministically generate every key
  in the wallet.
- **Passphrase (BIP39)** — optional extra secret combined with the seed; a different
  passphrase yields a different wallet.
- **Script type** — the address format standard (Legacy/BIP44, Wrapped SegWit/BIP49, Native
  SegWit/BIP84, Taproot/BIP86).
- **xpub / xprv** — extended public/private key of the wallet's account; the xpub watches,
  the xprv spends. SLIP-132 variants: ypub/zpub etc.
- **WIF** — Wallet Import Format: a single address's private key as text.
- **UTXO** — Unspent Transaction Output: a discrete "coin" the wallet can spend.
- **Change** — the portion of spent UTXOs returned to your own (internal) addresses.
- **Coinbase** — the transaction paying the miner of a block; spendable after 121
  confirmations.
- **Mempool** — the set of broadcast-but-unconfirmed transactions.
- **Gap limit** — how many consecutive unused addresses the wallet scans past before
  stopping (20).
- **PSBT** — Partially Signed Bitcoin Transaction: the standard interchange format for
  signing a transaction on a device other than the one that built it.
- **RBF** — Replace-By-Fee: a signal that a transaction may be replaced by a higher-fee
  version while unconfirmed.
- **TOFU** — Trust On First Use: pinning a server's TLS certificate at first contact and
  refusing silent changes afterwards.
- **Watch-only** — a wallet holding only public keys: full visibility, zero spending
  ability.

---

*This guide documents observed behavior of the software and is kept in sync with the code.
For the formal threat model see [SECURITY.md](SECURITY.md); for per-release changes see
[CHANGELOG.md](CHANGELOG.md).*
