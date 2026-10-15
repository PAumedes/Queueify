# Changelog

All notable changes to Queueify. Versions follow [Semantic Versioning](https://semver.org/).

Each release section is written by `tools\release.ps1` from the
[Conventional Commits](https://www.conventionalcommits.org/) since the previous tag. Don't edit released
sections by hand; to change what the next release says, write better commit messages.

## [1.5.0] - 2026-10-15

### Features

- **web:** account prefs in the shell, tour, /add?q, user data deleted (7ed3e5e)
- **web:** translated account pages, CSP, atomic first registration (a70f828)
- **web:** Settings page with write-only keys, key test and data tools (fae56d8)
- **web:** Photo page with line boxes and a link to the Add search (da632eb)
- **web:** export and defensive import of the queue (fe99034)
- **web:** restyle identity pages, add registration rule (ddd04b1)
- **web:** settings and preferences services (51fd278)
- **web:** fluent-token shell, nav, queue and add pages, PWA (0c30696)
- **web:** trusted proxy settings and a 90-day tombstone purge (6310bb8)
- **web:** strings for Settings, Help, Photo and data pages (c759aa8)
- **web:** queue view, play links, user queue, search and prefs services (6a6d91e)
- **web:** sync server, personal access tokens and the connect page (707c8f7)
- **web:** photo reading with Tesseract and encrypted secrets (cef9cdb)
- **sync:** desktop client for the Queueify web server (bd59513)
- **core:** IOcr, a Windows adapter and CleanLine on net10.0 (3948616)
- **web:** sync contract, connect text and per-user stores (0ecf84e)
- **web:** scaffold Queueify.Web, Core on net10.0, ADR-0010 (4f74c0a)

### Fixes

- **web:** review follow-ups: health check host, password field, notes (1f2dabd)
- **web:** album years are 1000 to 3000; clear AlbumSearch warnings (2e5e43d)
- **web:** deleting a user's data goes through the sync engine (3e75ac0)
- **web:** sync 401 and 429 answers say what to do next (032e0da)
- **web:** create the import temp file readable by its owner only (cc605c4)
- **web:** tighten the CSP, allow blob previews, public connect address (0137622)
- **web:** force Secure cookies outside Development (dd07219)
- **web:** sync tokens need the password and end with a credential change (cbaec30)
- **web:** deleting the last account no longer reopens registration (54653c1)
- **core:** keep sync pages going, one transaction per page, UTC stamps (4a7adff)
- **web:** keep the account database in the data folder, not in git (fbdb099)
- **web:** registration confirmation answers the same for all addresses (18d0d7c)
- **web:** decode photos with SkiaSharp so Release publish works (193aa89)
- **web:** load the component stylesheet bundle again (3c87f1f)
- **web:** cover tiles on phones, account pages in the shell (7cdf2ca)
- **web:** imported albums are queued for sync so PCs receive them (214203d)
- **web:** review round 1 on import, photo flow and settings notices (4f97f79)
- **web:** stale-tab writes, stuck search, guarded saves (3b8aff8)
- **web:** lockout, search throttle, antiforgery on prefs, a11y fixes (3694feb)
- **web:** cover switch label outside the web component (7381ef1)
- **web:** theme sync, menus close, phone layout from screenshots (1182e4b)
- **web:** key scan covers razor pages and skips web tests (880f4fc)
- **sync:** reject unusable serverTime, cut connections are offline (2307737)
- **web:** row quota, no-store, stricter user ids, review fixes (c6a7a06)
- **sync:** bound web server data, clearer reconnect, SECURITY rows (ee7d574)

### Performance

- **web:** a shared search link searches once, after the page is live (25da7b3)
- **web:** parse prefs.json once and skip static files in the shell (7ebe5ad)
- **web:** queue changes update memory; filter is debounced and paged (6537fb2)

### Documentation

- **web:** hosting guide follows the review (5fa35b5)
- link the hosting guide from the README (1769881)
- **web:** Dockerfile and hosting guide (b9593bd)
- name the MainWindow partial classes (7c06ae5)
- secrets.dat, DataDir and the key ring limit (be09157)
- ADR-0011, architecture, README and SECURITY for the web pages (833fe53)

### Tests

- drop two weak UTC tests; ci: absolute test-results paths (0ceacba)
- **core:** pin matcher scores and merge rule, drop sleeps (fd22590)
- run the real ServerSync client against the real SyncEngine (6d9c422)
- **web:** the migration snapshot matches the model the app builds (606500a)
- **web:** one test at a time and a private account db per host (15690b3)
- **web:** component tests for queue, add and navigation (b9297c3)
- **web:** fix the quota and model tests so they build (11b2434)

### Refactoring

- **web:** token service uses a context factory, small fixes (eecaf54)
- **app:** split MainWindow into partial classes by area (c4ce406)
- **core:** SyncSetup tries a connection on a copy of the settings (6ea5b21)
- **app:** use QueueView for filter, saved lookup and typed albums (fe199af)

### Build and CI

- shared Directory.Build.props and central package versions (a9b7414)
- build and test the web projects, cache NuGet, time limits (754891e)

### Other

- ignore scratch folders (1073e48)
- **web:** newer AngleSharp for the bUnit tests (advisory) (7efc61d)

## [1.4.0] - 2026-10-14

### Features

- a loading screen, and updates in About with a dot on Help (fa16a86)
- update Queueify from inside the app (3f61040)

### Fixes

- the starter's messages come to the front (736363b)
- update news reaches the user on any page (907a8ef)
- when the new exe won't start, point to a version that works (a736ff6)
- skip a version, and restart without a time limit (0b22a79)
- replace the exe in one step, with no copy beside it (a54187a)
- never free the exe's name, and leave focus where the user is (a8d255e)
- make updating safe and clear when things go wrong (a9268ee)
- **build:** refuse an archive header the packer never writes (df0cb85)

### Documentation

- skipping a version and the restart wait (22a3968)

### Build and CI

- pack the app with LZMA and the x86 branch filter (165aed5)

## [1.3.0] - 2026-10-13

### Features

- download Queueify as a 21 MB exe compiled to native code (dd9d11f)
- **ui:** reach a magazine photo's lines by keyboard and screen reader (ed28469)

### Fixes

- **build:** check the download before unpacking, and tidy old copies (f3d89b2)
- **build:** check the unpacked app and survive two starts at once (0d45365)

### Refactoring

- show settings lists through compiled bindings (a29fdf4)

## [1.2.0] - 2026-10-13

### Features

- keep the queue in local app data, or next to the exe when portable (b46697f)
- download Queueify as one 35 MB exe instead of a 93 MB zip (3a8fe6f)

### Fixes

- ignore a LOCALAPPDATA with characters no folder can have (0e659e9)
- use only Queueify's own Data folder, and show search results first (cddca0f)
- **ui:** put the optional Magazine field under the search (7918083)
- **ui:** load album covers only from web addresses (bc66d7a)
- **build:** start the downloaded exe under any file name (2a50824)

### Documentation

- moving from 1.1.0, the unpacked copy and where to keep the exe (8443bd6)

### Tests

- require a window from the download, and run UI tests on any name (d3f5387)

### Refactoring

- keep settings, covers and Data working when trimmed (1ac4034)

## [1.1.0] - 2026-10-13

### Features

- **i18n:** speak Spanish and English, chosen in Settings (726aac1)
- **ocr:** manage text recognition languages and explain every install outcome (1bfd95b)
- **settings:** turn Settings into a page with a card per service (7c1c2f7)

### Fixes

- **build:** plan releases in one tested step (bbb58de)
- **build:** a final release lists everything since the last one (1824032)
- **build:** sturdier release tool, checked emulator jar, standard zip (7fd212d)
- **i18n:** lowercase language names mid-sentence, and sturdier startup (7d2e5b4)
- **i18n:** regional formats, Spanish singulars and WinUI's own texts (c6d74ea)
- **ocr:** keep the window responsive during the permission prompt (44382f2)
- **ocr:** keep the offer to install after declining, and harden the installer (96dd66b)
- **settings:** blame the keys only when the service refused them (ce85013)
- **settings:** test keys honestly and keep broken keys removable (7413f06)
- **ui:** keep focus on the rail after Ctrl+B and fit large text sizes (51c08b5)
- **ui:** leave a rail where the hidden Add albums panel was (e85c9d6)

### Documentation

- add the project's engineering docs, license and templates (103c750)

### Tests

- **ocr:** decide "install or not" in a pure function (9aa5798)

### Build and CI

- build, test and release on GitHub Actions (daf24db)

## [1.0.0] - 2026-10-12

### Features

- Search Spotify and Tidal together, add albums from magazines, mark them listened and rate them
- Read magazine photos with Windows' text recognition
- Keep the same queue on several PCs through your own Firebase project
- Quick tour, Help and About
