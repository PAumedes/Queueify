# CLAUDE.md

Queueify: a portable WinUI 3 app (.NET 10, Windows App SDK, unpackaged) that keeps a listen-later queue of
albums recommended in magazines, searching Tidal and Spotify, reading magazine photos with Windows OCR and
syncing through the user's own Firestore. English and Rioplatense Spanish. GPL-3.0-or-later.

## Read before working

- Architecture: @docs/ARCHITECTURE.md
- Engineering rules and definition of done: @docs/ENGINEERING.md
- Topic docs as needed: `docs/TESTING.md`, `docs/RELEASING.md`, `docs/AGENT_WORKFLOW.md`, `docs/adr/`

## Commands

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"   # if the SDK isn't on PATH
dotnet test --project Queueify.Tests                      # unit tests, the TDD loop
tools\firestore-emulator.ps1 -Background; $env:QUEUEIFY_FIRESTORE_EMULATOR = '127.0.0.1:8080'
tools\package.ps1                                         # dist\Queueify\Queueify.exe + versioned copy (AOT, ~5 min)
powershell -ExecutionPolicy Bypass -File tests\ui\Queueify.UiTests.ps1 -Exe dist\Queueify\Queueify.exe
tools\release.ps1 -Preview                                # next changelog
```

## Non-negotiables

- **TDD.** Failing test first, failing for the right reason. Never weaken a test to go green.
- **Logic in `Queueify.Core`**, which has no WinUI references; decisions worth testing become pure functions there.
- **Every text through the string tables** (`Loc.T`, `{l:Tr Key=…}`), in English and Spanish.
- **Adversarial review after every change**: a separate agent reviews the commit
  (`.claude/skills/adversarial-review`); blockers and majors are fixed test-first before merging.
- **Never touch the user's real data.** Build and run scratch copies elsewhere; the UI tests back up
  `Data` themselves.
- **Docs in the same change**: README, `docs/`, ADRs and the in-app Help.
- **Comments explain why.** No prompt-like or AI-addressed text anywhere.
- **Never** push, tag, release or change repository settings unless the maintainer asks.

## Environment notes

- The dev PC runs Windows 11 IoT Enterprise LTSC (no winget, no Microsoft Store). Git and the GitHub CLI
  are portable installs in `%LOCALAPPDATA%\Programs`.
- Native AOT needs the C++ build tools (installed: VS 2022 Build Tools, VCTools workload); package.ps1
  puts vswhere on PATH for it.
- Windows PowerShell 5.1 reads `.ps1` files as ANSI unless they have a UTF-8 BOM: save scripts with
  non-ASCII text with a BOM.
