# 0010. TOFU certificate pinning for indexing servers

- **Status:** Accepted
- **Area:** Network
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Indexing servers commonly use self-signed certificates, so CA validation is not available, yet silent MITM substitution must still be detectable.

## Decision

Trust On First Use: the certificate fingerprint is pinned on first successful TLS connection (`CertificatePinStore`, persisted per network in `server-certs.json`). A later mismatch raises `CertificatePinMismatchException` and blocks the connection until the user explicitly resets the pin (GUI, or CLI `reset-certs`). A corrupted pin file must not permanently block reconnection.

## Consequences

- Detects certificate substitution after first contact.
- Does not protect the first connection; users cannot distinguish a legitimate rotation from an attack, so the reset is deliberately manual.
- Reflects `SECURITY.md`; any change to the pinning policy must update it in the same commit.

## References

- `src/Core/Net/CertificatePinStore.cs`
- `SECURITY.md`
