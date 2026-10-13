---
name: tdd-cycle
description: The red-green-refactor procedure and test conventions for Queueify (xUnit v3 for Queueify.Core, UI Automation scripts for the app). Use whenever writing or changing tests or production code here.
---

# TDD cycle

The full policy is in `docs/TESTING.md`. This is the checklist.

```text
1. Pick the next smallest behaviour.
2. RED       write one test → dotnet test --project Queueify.Tests → fails for the expected reason?
             (UI: add the check to tests\ui\Queueify.UiTests.ps1 and run it against the current build)
3. GREEN     least code that passes
4. REFACTOR  in small steps, tests green after each
5. Repeat. Before committing: every suite the change touches.
```

## Where tests go

| Code | Test |
| --- | --- |
| `Queueify.Core\…` | `Queueify.Tests\<Area>Tests.cs` |
| Sync, rules | `Queueify.Tests\CloudSyncTests.cs` (emulator-gated) |
| What the window shows or does | `tests\ui\Queueify.UiTests.ps1` (`Check (...) 'what'`) |
| Release tooling | `tests\tools\Release.Tests.ps1` |

## Red flags before handing off

- Could the test pass with a wrong implementation? Mutate the code mentally.
- Does it depend on this PC (Windows language, regional format, installed OCR languages, network)? Pin
  it (`Loc.Override("en")`), fake it (`HttpMessageHandler`), or skip with a reason (`Assert.SkipWhen`).
- Could a regression start something real (an elevated install, a network call)? Test a pure decision
  function instead.
