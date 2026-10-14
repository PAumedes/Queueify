# ADR-0009: Update from GitHub releases, inside the app

- Status: Accepted
- Date: 2026-10-14

## Context

Updating meant noticing a new release, downloading it and replacing the exe by hand, keeping its name so a
taskbar pin still works. The download is a single exe ([ADR-0008](0008-native-aot-and-a-starter-exe.md)),
and while the app runs nothing has that exe open: the starter unpacks the app, starts it and exits.

## Decision

- At each start (unless turned off in Settings → Updates) and from **Help → About → Check for updates**, the
  app asks GitHub's API for the latest release (`api.github.com/repos/PAumedes/Queueify/releases/latest`, which
  skips drafts and pre-releases). It's offered only when its version is higher and it has an asset named
  `Queueify-<version>-win-x64.exe` under `https://github.com/PAumedes/Queueify/releases/download/` with a
  `sha256:` digest (GitHub computes one for every release asset).
- The offer is a bar under the page title, in a row of its own. Nothing is downloaded until the user clicks
  **Update and restart**. Then the exe is downloaded next to the one the user started, always into a new
  file (`<exe>.download`; a file already there is removed first, and if it can't be, nothing is updated; at
  most 256 MB; a download that sends nothing for 30 s gives up, a slow one doesn't), its SHA-256 compared
  with GitHub's, and the two swapped by renaming: the old one becomes `<exe>.old`, removed at the next start;
  if the new one can't be moved in, the old one goes back. A read-only exe is updated too.
- The app then restarts through the new exe, and its window stays (saying so) until the new version's
  window is open: the first start of a version unpacks for a few seconds.
- A failed update says why in plain words and offers **Try again** and the release page.
- When the folder can't be written to, or the app wasn't started from a download (a build folder), the bar
  offers the release page instead.
- `QUEUEIFY_UPDATE_SERVER` points the check at a server on this PC (loopback `http` only, anything else is
  ignored), for the UI tests (`tests\ui\Queueify.UpdateUiTests.ps1`).

## Consequences

- One request to GitHub per start: GitHub sees the address and that Queueify is starting, nothing else;
  the setting turns it off. GitHub allows 60 unauthenticated requests an hour per address, plenty for a few
  starts a day.
- A closed offer comes back at the next start; there's no "skip this version", only the setting.
- The release's asset name and its digest are now a contract: renaming the asset, or publishing it somewhere
  else, breaks updating for everyone on older versions ([RELEASING.md](../RELEASING.md)).
- The trust is GitHub's: the digest comes from the same API as the download link, over HTTPS. It catches a
  broken or tampered download, not a release published by someone who took over the maintainer's account.
  Signing the exe would cover that (see ADR-0008 about where a signature would go).
- The downloaded exe has no "downloaded from the internet" mark, so SmartScreen doesn't ask again.
- 1.3.0 and earlier can't update themselves: 1.4.0 has to be downloaded once by hand.

## Alternatives considered

- **A separate updater exe**: not needed, since the exe isn't in use while the app runs.
- **Only a notice with a link**: simpler, but leaves the rename-and-keep-the-name dance to the user.
- **Checking once a day**: more state to keep for little saving.
