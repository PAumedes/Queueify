# ADR-0008: Native AOT and a starter exe

- Status: Accepted
- Date: 2026-10-13
- Supersedes the packaging part of [ADR-0007](0007-single-trimmed-exe.md) (where the data lives still stands)

## Context

1.2.0's download was one 35 MB .NET single-file exe that unpacked about 70 MB into `%TEMP%\.net`. The aim is
the smallest single exe that needs nothing installed. Measured on GitHub's runners and locally (13 Oct 2026):

| Build | Download | Needs | Starts |
| --- | --- | --- | --- |
| .NET single-file, trimmed (1.2.0) | 35 MB exe | nothing | yes |
| Native AOT folder, zipped | 28 MB zip, 241 files | nothing | once ported |
| Native AOT folder, 7-Zip (LZMA) | 15.5 MB archive | nothing | once ported |
| Native AOT, Windows App Runtime installed separately | 6.4 MB zip | the 46 MB runtime, per PC | once ported |
| **Native AOT folder appended to a starter, Brotli** | **~21 MB exe** | **nothing** | **yes** |

About 50 MB of the unpacked app is the Windows App SDK's own native code (WinUI), which Native AOT can't merge
into the exe and which a PC without the runtime must get from the download.

## Decision

- **Native AOT** for the app (`PublishAot` in Release): native code, no .NET runtime shipped, faster start.
  Porting it took three things:
  - `CsWinRTAotOptimizerEnabled` and `AllowUnsafeBlocks`: the WinRT interop code WinUI needs is generated at
    build time, and for collections only with unsafe code allowed; without it the generator skips lists
    silently and the app fails at start ("Value does not fall within the expected range").
  - Every `ItemsSource` gets a `List<object>` built in the app project (`MainWindow.Items`): interop code is
    generated for lists made there, not for those Queueify.Core returns.
  - `{x:Bind}` templates instead of `DisplayMemberPath` (property lookup at run time).
- **A starter exe** (`Queueify.Launcher`, itself Native AOT, ~2.4 MB) with the published app appended as one
  Brotli-compressed tar (quality 11, 16 MB window) and a trailer with the archive's length and a hash id.
  It unpacks once per version into `%LOCALAPPDATA%\Queueify\app\<id>` (beside, then renamed into place, so a
  crash or a second copy never leaves half an app), starts it with `QUEUEIFY_EXE` set to itself, and
  removes other versions' folders when nothing runs from them.
- The app treats `QUEUEIFY_EXE` as the exe the user started: a portable `Data` folder next to the download
  still counts, and the window's taskbar relaunch command points at the download, so a pin survives updates.
- The starter resolves its imports from System32 only (`/DEPENDENTLOADFLAG:0x800`): `bcrypt.dll` isn't a
  KnownDLL, and the download may sit in a folder others can write to.

## Consequences

- One 21 MB exe (from 35 MB, and 93 MB in 1.1.0), nothing to install; later starts skip the unpacking.
- Building needs Visual Studio's C++ build tools, and a release build takes about 5 minutes (AOT and Brotli
  at its strongest). GitHub's Windows runners have them.
- `crash.log` stack traces name methods but have no file and line (the symbols would be a 50 MB file).
- A new place users may wonder about: `%LOCALAPPDATA%\Queueify\app`, about 70 MB, documented in the README.
- Code that needs run-time reflection or WinRT interop the generator didn't see fails only at run time. The
  UI tests run the download itself (lists, settings, sync, the language restart, reading a pasted photo), and
  `tools\package.ps1` checks the start, the data location, the taskbar pin and the starter's dll loading.
- Self-unpacking exes are sometimes flagged by antivirus heuristics; none was seen with Windows Defender.

## Alternatives considered

- **Keep .NET single-file (ADR-0007)**: 35 MB, and it unpacks the same amount.
- **LZMA (7-Zip) instead of Brotli**: about 3 MB smaller, but needs a third-party decoder in the starter.
  Windows' own LZMS (Compression API) came out larger than Brotli (23.1 MB against 18.4 MB for the app).
- **Windows App Runtime installed separately**: a 6.4 MB app, but a 46 MB prerequisite on each PC.
- **Trimming WinUI's other languages**: 0.4 MB, not worth diverging from what the SDK ships.
