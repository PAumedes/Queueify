# Changelog

All notable changes to Queueify. Versions follow [Semantic Versioning](https://semver.org/).

Each release section is written by `tools\release.ps1` from the
[Conventional Commits](https://www.conventionalcommits.org/) since the previous tag. Don't edit released
sections by hand; to change what the next release says, write better commit messages.

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
