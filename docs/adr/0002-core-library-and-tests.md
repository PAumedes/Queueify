# ADR-0002: Logic in a UI-free core library, tested first

- Status: Accepted
- Date: 2026-10-12

## Context

WinUI code is awkward to unit-test: controls need a dispatcher and a window. The first version kept its
logic in the window class and was checked with throwaway scripts, which missed bugs found later (sync
conflicts, settings uploaded the wrong way).

## Decision

Everything that isn't UI lives in `Queueify.Core` (no WinUI references) and is developed test-first with
xUnit v3 on Microsoft Testing Platform. The UI is covered by PowerShell scripts that drive the published
app through Windows UI Automation. When UI code grows a decision worth testing, the decision moves to Core
as a pure function.

## Consequences

- Fast, deterministic unit tests; sync is tested against the real Firestore emulator.
- UI tests are slower and need an interactive desktop, so they run locally, not in CI.
- `MainWindow` remains large; splitting it into pages is a refactor for later.

## Alternatives considered

- **MVVM with a test framework for view models**: more structure than a one-window app needs today.
- **WinAppDriver / Appium**: heavier to set up than UI Automation from PowerShell for the same checks.
