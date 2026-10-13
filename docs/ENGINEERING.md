# Engineering principles

These rules apply to every change. Reviewers (human and agent) cite them by section.

## 1. Less is more

- Every line has to earn its place. Delete before adding.
- No speculative generality: no option, abstraction or parameter without a current caller.
- One way to do each thing.

## 2. Correctness first

- **Logic lives in `Queueify.Core`** and is unit-tested there. The WinUI project wires controls to it;
  when a decision grows a branch worth testing (what to offer after an install, which country to show),
  it moves to Core as a pure function.
- **External data is untrusted**: JSON from Spotify, Tidal and Firebase is read defensively, and a bad
  answer becomes a `CatalogException` with a message the user can act on, never a crash.
- **No exception escapes an event handler.** `async void` handlers catch and show what happened; unhandled
  ones are written to `crash.log` in the data folder.
- **Undo never overwrites newer data**: undo re-reads the stored version (a sync may have changed it).

## 3. Data and sync

- The SQLite database is the source of truth on each PC. Every change marks the album dirty; sync uploads
  dirty rows and downloads newer ones (newest `UpdatedAt` wins, deletions are soft until uploaded).
- Shared settings (keys, Play app, country) sync as one set; theme, language and layout are per PC.
- Only fields the user actually changed count as edits (`AppSettings.ApplyEdits`), so saving unchanged
  values never pushes older ones to the other PCs.
- The Firestore security rules (`tools/firestore.rules`) are tested against the emulator. Changing them
  means users must publish them again: say so in the release notes.

## 4. The UI thread

- Network, disk-heavy work and anything that can block (the elevation prompt) run off the UI thread;
  results come back to it. Timeouts are explicit (20 s for HTTP).
- One dialog at a time (`ShowDialogAsync`); repeated clicks must not open a second one.

## 5. Security

The threat model is in [SECURITY.md](../SECURITY.md). In short: no telemetry; keys stay on the user's PCs
and their own Firebase project; anything that runs elevated takes only allow-listed inputs, and paths
from the user's profile reach it Base64-encoded.

## 6. Internationalisation and accessibility

- Every user-visible text comes from the string tables (`Loc.T`, `{l:Tr Key=…}`), in English and Spanish.
- Dates and numbers use `Loc.Culture` (the user's regional format when it's in the app language).
- Every icon-only button has `AutomationProperties.Name` and a tooltip. Everything works by keyboard.
- Never use colour alone to convey state.

## 7. Definition of done

A change is done when:

1. Tests were written first and failed for the right reason ([TESTING.md](TESTING.md#tdd-cycle)).
2. Unit tests pass, with the Firestore emulator if sync code changed.
3. The UI tests pass if the UI changed (both, if sync changed).
4. The adversarial review ([AGENT_WORKFLOW.md](AGENT_WORKFLOW.md)) has no open blocker or major finding.
5. Docs, ADRs and the in-app Help are updated in the same pull request. The changelog comes from commit
   subjects at release time ([ADR-0006](adr/0006-release-from-conventional-commits.md)).
6. UI changes were looked at in light and dark, in English and Spanish.
