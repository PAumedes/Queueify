# Testing

## Suites

| Suite | Runs | Where |
| --- | --- | --- |
| Unit (`Queueify.Tests`) | Core logic: matching, store, sync, catalogs (fake HTTP), OCR, texts, settings | `dotnet test --project Queueify.Tests`; CI |
| Sync (inside the unit suite) | Two simulated PCs against the Firestore emulator, and the security rules | with `QUEUEIFY_FIRESTORE_EMULATOR`; CI |
| UI (`tests\ui\Queueify.UiTests.ps1`) | The published app through UI Automation: tour, pages, panel, settings, language restart | locally, before merging UI changes |
| Sync UI (`tests\ui\Queueify.SyncUiTests.ps1`) | Connecting Sync from the dialog, an album reaching Firestore | locally, with the emulator |
| Release tool (`tests\tools\Release.Tests.ps1`) | Version bump, changelog and notes | locally; CI |

The OCR test reads `Queueify.Tests\TestData\magazine-page.png`. It's skipped on machines with no text
recognition language (some build servers).

## Running

```powershell
dotnet test --project Queueify.Tests

tools\firestore-emulator.ps1 -Background          # Java 21+; downloads the jar once into .cache\
$env:QUEUEIFY_FIRESTORE_EMULATOR = '127.0.0.1:8080'
dotnet test --project Queueify.Tests               # now with the sync tests

tools\package.ps1                                  # dist\Queueify\Queueify.exe
powershell -ExecutionPolicy Bypass -File tests\ui\Queueify.UiTests.ps1 -Exe dist\Queueify\Queueify.exe
powershell -ExecutionPolicy Bypass -File tests\ui\Queueify.SyncUiTests.ps1 -Exe dist\Queueify\Queueify.exe
```

The UI tests move the `Data` folder next to the exe aside and restore it afterwards, even when they fail;
the backup folder is only deleted once every file is back. They pin the app to English so they work on
any Windows language, and close only the copy of Queueify they started. Leave the mouse and keyboard
alone while they run.

## TDD cycle

1. Pick the smallest next behaviour.
2. **Red**: write one test and run it. It must fail for the expected reason (a compile error for a
   missing member counts; a test that passes straight away is suspect).
3. **Green**: the least code that passes.
4. **Refactor** under green tests, in small steps.
5. Before handing off: all suites that the change touches.

## Conventions

- Test names read as specifications: `Precheck_DecidesWithoutStartingAnything`.
- No real network, clock or elevation in unit tests: HTTP goes through a fake `HttpMessageHandler`, and
  decisions that would start something (an install) are pure functions tested on their own.
- Texts asserted in English run under `Loc.Override("en")` when they contain numbers or dates, so the
  machine's regional format can't change them.
- Could the test pass with a wrong implementation? Flip a comparison mentally; a test should fail.
