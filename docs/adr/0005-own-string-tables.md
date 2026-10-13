# ADR-0005: Our own string tables for English and Spanish

- Status: Accepted
- Date: 2026-10-13

## Context

The app needs English and Spanish, chosen in Settings. WinUI's resource system (`.resw` files and
`x:Uid`) is built for packaged apps and is hard to test; .NET `.resx` files need satellite assemblies and
make it hard to see both languages side by side.

## Decision

Two C# dictionaries in Core (`Strings.En.cs`, `Strings.Es.cs`) behind `Loc.T(key)` and `Loc.F(key, …)`,
and a XAML markup extension `{l:Tr Key=…}`. The language is set once at startup (`Loc.Use`, plus WinUI's
`PrimaryLanguageOverride` for the controls' own texts); changing it restarts the app. Dates and numbers
use the user's regional format when it's in the app language.

Tests check that both tables have the same keys and placeholders, that no key is defined twice, and that
every key used in the code and XAML exists.

## Consequences

- Translations are plain code, reviewed in the same pull request as the feature.
- No live language switch: XAML reads its texts while loading, so a restart applies the change.
- A third language means a third table and a choice in Settings.

## Alternatives considered

- **`.resw` + `x:Uid`**: the WinUI way, but awkward unpackaged and untestable from Core.
- **`.resx`**: standard .NET, but satellite assemblies and no compile-time view of both languages.
