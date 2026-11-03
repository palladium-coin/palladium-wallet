# 0001. Record architecture decisions as ADRs

- **Status:** Accepted
- **Area:** Process
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

Design choices in this wallet (SPV trust model, key handling, storage format) have direct financial impact and are easy to forget or to undo by accident. The reasoning currently lives in code comments, `SECURITY.md` and `CHANGELOG.md`, which record *what* but rarely *why one option was chosen over another*.

## Decision

Keep one Architecture Decision Record per feature in `docs/adr/`, in Markdown, versioned with the code and reviewed in the same pull request as the change they describe. Format and workflow are defined in `docs/docs-as-code.md` and `docs/adr/template.md`. An accepted ADR is never rewritten: a changed decision gets a new ADR that supersedes the old one.

## Consequences

- The rationale survives contributor turnover and is reviewable like code.
- Decisions recorded retroactively (0002 onwards) reconstruct intent from code, comments and the changelog; they are marked as such and should be corrected if the original author remembers differently.
- Small overhead per feature: an ADR is expected when a decision is hard to reverse or touches cryptography, SPV validation, key handling or the file format.

## References

- `docs/docs-as-code.md`
- `docs/adr/template.md`
- `docs/check-docs.sh`
