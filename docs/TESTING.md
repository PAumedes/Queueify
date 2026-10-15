# Testing

## Suites

| Suite | Runs | Where |
| --- | --- | --- |
| Unit (`Queueify.Tests`) | Core logic: matching, store, sync, catalogs (fake HTTP), OCR, texts, settings | `dotnet test --project Queueify.Tests`; CI |
| Web sync client (inside the unit suite) | `ServerSync` against scripted answers over a fake `HttpMessageHandler`: round trips, pagination (a full page means more), 401, timeout, redirects, size limit, token never in messages | always on |
| Sync (inside the unit suite) | Two simulated PCs against the Firestore emulator, and the security rules | with `QUEUEIFY_FIRESTORE_EMULATOR`; CI |
| UI (`tests\ui\Queueify.UiTests.ps1`) | The published app through UI Automation: tour, pages, panel, settings, language restart | locally, before merging UI changes |
| Web (`Queueify.Web.Tests`) | The sync server and tokens through `WebApplicationFactory<Program>`: auth, user isolation, conflicts, limits, secrets on disk, and `ServerSyncEndToEndTests`, which runs the real `ServerSync` client against the real `SyncEngine` in process (two PCs and the web, conflicts, deletions, pages over 1,000, revoked token) | `dotnet test --project Queueify.Web.Tests`; CI |
| Sync UI (`tests\ui\Queueify.SyncUiTests.ps1`) | Connecting Sync from the dialog, an album reaching Firestore | locally, with the emulator |
| Update UI (`tests\ui\Queueify.UpdateUiTests.ps1`) | A copy of the download updating itself from a release served on this PC: offer, refusing a bad download, the setting, the swap and restart | locally |
| Release tool (`tests\tools\Release.Tests.ps1`) | Version bump, changelog and notes | locally; CI |

The OCR test reads `Queueify.Tests\TestData\magazine-page.png`. It's skipped on machines with no text
recognition language (some build servers).

## Running

```powershell
dotnet test --project Queueify.Tests

tools\firestore-emulator.ps1 -Background          # Java 21+; downloads the jar once into .cache\
$env:QUEUEIFY_FIRESTORE_EMULATOR = '127.0.0.1:8080'
dotnet test --project Queueify.Tests               # now with the sync tests

tools\package.ps1                                  # dist\Queueify\Queueify.exe (the download, ~5 min)
powershell -ExecutionPolicy Bypass -File tests\ui\Queueify.UiTests.ps1 -Exe dist\Queueify\Queueify.exe
powershell -ExecutionPolicy Bypass -File tests\ui\Queueify.SyncUiTests.ps1 -Exe dist\Queueify\Queueify.exe
powershell -ExecutionPolicy Bypass -File tests\ui\Queueify.UpdateUiTests.ps1 -Exe dist\Queueify\Queueify.exe
```

The UI tests run the download itself, in portable mode: they move the `Data` folder next to the exe aside
and restore it afterwards, even when they fail; the backup folder is only deleted once every file is back.
They point `LOCALAPPDATA` at a scratch folder, so the app the starter unpacks never lands in the real one,
and follow the app's own process from there. They pin the app to English so they work on any Windows
language, and close only the copy of Queueify they started. The photo check puts the test photo on the
clipboard. Leave the mouse and keyboard alone while they run.

`tools\package.ps1` checks what the UI tests can't: the download starts under another name from an empty
folder, keeps its data in local app data (default mode), and a taskbar pin would start the downloaded exe.

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
- Web tests never touch real data or the source tree: every host (smoke test included) points `Queueify:DataDir` and the Identity database at a scratch
  folder (`SyncFactory`, `ScratchHost`) and gives every test its own user, so tests share one server without seeing each other's data.
- Could the test pass with a wrong implementation? Flip a comparison mentally; a test should fail.

## Build rules

`Directory.Build.props` holds the settings every project shares (nullable, implicit usings) and, when `CI=true`
(GitHub sets it), turns nullable warnings and NuGet audit findings (known vulnerable packages) into errors; set
`CI=true` locally to see what CI will see. `Directory.Packages.props` holds every package version (central package
management): projects reference packages without a `Version`. The app version stays in `Queueify\Queueify.csproj`
and `Queueify.Core\Queueify.Core.csproj`, where `tools\package.ps1` and `release.yml` read it. CI also publishes
`Queueify.Web` as a smoke step, because the tests do not notice a publish that no longer works.
