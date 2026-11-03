# Architecture Decision Records

One ADR per feature or significant design decision. Format: `template.md`. Workflow: `../docs-as-code.md`.

| # | Decision | Area | Status |
|---|----------|------|--------|
| 0001 | [Record architecture decisions as ADRs](0001-record-architecture-decisions.md) | Process | Accepted |
| 0002 | [One shared Avalonia UI with thin platform heads](0002-shared-avalonia-ui-with-platform-heads.md) | Architecture | Accepted |
| 0003 | [Layered architecture: Core knows nothing about the UI](0003-core-without-ui-dependency.md) | Architecture | Accepted |
| 0004 | [Centralize all chain constants in a network profile](0004-centralized-network-profile.md) | Chain | Accepted |
| 0005 | [Use NBitcoin for protocol primitives, hand-write only the rest](0005-nbitcoin-for-protocol-primitives.md) | Crypto | Accepted |
| 0006 | [Anchor headers to hardcoded checkpoints and skip PoW validation](0006-spv-checkpoint-anchoring-without-pow.md) | SPV | Accepted |
| 0007 | [Verify a Merkle proof for every confirmed transaction](0007-mandatory-merkle-verification.md) | SPV | Accepted |
| 0008 | [Progressive verification: show early, spend only after proof](0008-progressive-verification-sync.md) | SPV | Accepted |
| 0009 | [Hand-written Electrum JSON-RPC client with pipelining and an in-flight cap](0009-custom-electrum-client.md) | Network | Accepted |
| 0010 | [TOFU certificate pinning for indexing servers](0010-tls-tofu-certificate-pinning.md) | Network | Accepted |
| 0011 | [Server registry seeded by bootstrap list and peer discovery](0011-server-registry-and-discovery.md) | Network | Accepted |
| 0012 | [Wallet file encrypted with AES-256-GCM and PBKDF2-HMAC-SHA512](0012-encrypted-wallet-file.md) | Storage | Accepted |
| 0013 | [Versioned JSON wallet document with an offline sync cache](0013-versioned-wallet-document-and-sync-cache.md) | Storage | Accepted |
| 0014 | [BIP32/39 HD wallets with BIP44/49/84 paths and SLIP-132 keys](0014-hd-derivation-and-slip132.md) | Crypto | Accepted |
| 0015 | [Imported-key and watch-only accounts](0015-imported-keys-and-watch-only-accounts.md) | Wallet | Accepted |
| 0016 | [Route every signing flow through PSBT](0016-psbt-centric-signing.md) | Wallet | Accepted |
| 0017 | [Coin selection, fee and confirmation policy](0017-coin-selection-fee-and-spendability-policy.md) | Wallet | Accepted |
| 0018 | [Exclusive OS-level lock on the open wallet file](0018-exclusive-wallet-file-lock.md) | Storage | Accepted |
| 0019 | [Resolve the data root by explicit precedence](0019-data-location-resolution.md) | Storage | Accepted |
| 0020 | [A single ViewModel split into partial classes by feature](0020-single-viewmodel-with-feature-partials.md) | UI | Accepted |
| 0021 | [In-app overlays instead of OS windows](0021-in-app-overlays-instead-of-windows.md) | UI | Accepted |
| 0022 | [In-code localization table for seven languages](0022-localization-seven-languages.md) | UI | Accepted |
| 0023 | [Best-effort update check against GitHub releases](0023-github-release-update-check.md) | Distribution | Accepted |
| 0024 | [A CLI front end on the same Core](0024-cli-on-shared-core.md) | Distribution | Accepted |
| 0025 | [Test the network layer against an in-process fake server](0025-in-process-fake-electrum-server-for-tests.md) | Testing | Accepted |
| 0026 | [Fuzz every untrusted-input parser](0026-fuzzing-untrusted-input-parsers.md) | Testing | Accepted |
| 0027 | [Server probe tool and opt-in live-server test](0027-server-probe-and-live-server-test.md) | Testing | Accepted |
| 0028 | [Reproducible release builds inside pinned Docker images](0028-reproducible-docker-release-builds.md) | Distribution | Accepted |
| 0029 | [Persistent Android release keystore kept out of git](0029-persistent-android-signing-keystore.md) | Distribution | Accepted |
| 0030 | [Single-source versioning with a mandatory changelog entry](0030-single-source-versioning-and-changelog.md) | Distribution | Accepted |
| 0031 | [Explicitly defer multisig, Lightning, Tor and hardware wallets](0031-defer-multisig-lightning-and-hardware-wallets.md) | Scope | Accepted |
| 0032 | [Run tests, fuzz smoke and docs checks in GitHub Actions](0032-github-actions-ci.md) | Testing | Accepted |
| 0033 | [Test the UI layer and CLI headlessly, gate coverage](0033-test-strategy-beyond-core.md) | Testing | Accepted |
| 0034 | [Bound every network request with a timeout](0034-bound-every-network-request.md) | Network | Accepted |
| 0035 | [Cap the length of a server line](0035-cap-server-line-length.md) | Network | Accepted |
| 0036 | [Verify the chain identity of a server right after connecting](0036-verify-chain-identity-on-connect.md) | SPV | Accepted |
| 0037 | [Derive an address private key only when it is revealed](0037-derive-address-private-keys-on-reveal.md) | Security | Accepted |
| 0038 | [CLI password sources and explicit plaintext storage](0038-cli-password-sources-and-explicit-plaintext.md) | Distribution | Accepted |

ADRs `0002` onwards were written retroactively on 2026-11-03 from the code, comments and changelog. 
If one misstates the original reasoning, fix it in a pull request.
