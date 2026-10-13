# ADR-0007: One trimmed exe as the download

- Status: Accepted
- Date: 2026-10-13

## Context

Release 1.1.0 was a 93 MB zip of 531 files (233 MB extracted), large for an app of lists and text boxes.
Measured in the 1.1.0 build:

| Part | Size | Why |
| --- | --- | --- |
| Windows App SDK AI, ML, search and widgets | ~60 MB | The `Microsoft.WindowsAppSDK` metapackage includes them; Queueify uses none |
| Windows API projections (`Microsoft.Windows.SDK.NET.dll`) | 25 MB | All of Windows' APIs; the app calls a handful |
| .NET runtime and libraries | 74 MB | Complete, though the app uses a fraction |

## Decision

- Reference `Microsoft.WindowsAppSDK.WinUI` (WinUI and what it depends on) instead of the metapackage.
- Publish **trimmed** and as a **single compressed file** (`PublishSingleFile`, with the Windows App SDK's
  `EnableMsixTooling`), so the release is one `Queueify-X.Y.Z-win-x64.exe` of about 35 MB.
- The data moves to `%LOCALAPPDATA%\Queueify`, so the exe can live anywhere, Downloads included. A `Data`
  folder next to the exe the user started (`Environment.ProcessPath`, not `AppContext.BaseDirectory`: a
  single-file app runs from a copy unpacked into the temporary folder) still wins, for portable copies and
  for 1.1.0's layout.
- Trimming turns off reflection-based JSON, so `settings.json` and `ui.json` go through a source-generated
  context (`StorageJson`); the tests run with reflection-based JSON off too. Covers use `{x:Bind}`; the
  country and text recognition lists still use `DisplayMemberPath` on records, which trimming keeps
  (the UI tests read the country back).
- The resource file is `resources.pri`, not `Queueify.pri`: WinUI finds `<exe name>.pri` or `resources.pri`,
  and the download is `Queueify-X.Y.Z-win-x64.exe` (or `Queueify (1).exe`).
- `tools\package.ps1` fails if the build is more than the exe or over 45 MB, and starts the download under
  another name from an empty folder, failing if it doesn't stay up.

## Consequences

- One file to download and keep; nothing to extract. From 1.2.0 on, updating is replacing the exe.
- **Not inside a 1.1.0 folder**: the .NET host loads `System.Private.CoreLib.dll` from the exe's folder, so
  1.1.0's untrimmed copy breaks the trimmed app before it can show anything. The README and the 1.2.0
  release notes say to use a new folder.
- The first start of each version takes a few seconds longer while it unpacks (about 70 MB, to
  `%TEMP%\.net\<exe name>\<build>`); later starts reuse it. Nothing removes old versions' copies.
- Debug information is embedded in the dlls, so `crash.log` stack traces keep file and line.
- Trimming can break code that relies on reflection without a build error. The trim analyzer is on in
  `Queueify.Core`, and the UI tests run against the trimmed exe; covers and settings are the known spots.
- The Windows SDK projections and WinRT runtime still report trim warnings (IL2104) of their own; the UI
  tests are what show they don't affect the app.

## Alternatives considered

- **Native AOT**: smaller and faster to start, but needs Visual Studio's C++ build tools on every build
  machine, and the Windows App SDK's native DLLs would still sit beside the exe (no single file).
- **Framework-dependent** (requires the .NET and Windows App SDK runtimes installed): a few MB, but users
  would have to install two runtimes first, against [ADR-0001](0001-portable-winui3-app.md).
- **An installer**: one file to download, but no longer portable.
