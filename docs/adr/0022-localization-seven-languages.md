# 0022. In-code localization table for seven languages

- **Status:** Accepted
- **Area:** UI
- **Recorded:** 2026-11-03 (retroactive: reconstructed from code, comments and changelog)

## Context

The wallet targets users across several markets and must work offline, with the strings available identically on every platform.

## Decision

`Loc` holds a key → 7-language table (it, en, es, fr, pt, de, zh). XAML uses `{Binding Loc[key]}`, C# uses `Loc.Tr("key")`. On language change the `Loc` instance is replaced so bindings refresh. An unknown language falls back to English. The picker in the Settings overlay is a hand-written list of `RadioButton`s bound to `IsLangXx` properties, so a new language needs a column, a button and a property.

## Consequences

- No resource-file tooling and no runtime lookup failures.
- Adding a language touches three places; forgetting the picker leaves it unreachable.

## References

- `src/App/Localization/Loc.cs`
- `src/App/ViewModels/MainWindowViewModel.Settings.cs`
