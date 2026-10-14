# ADR-0008: Native AOT and a starter exe

- Status: Accepted
- Date: 2026-10-13; compression changed to LZMA with the x86 branch filter for 1.4.0
- Supersedes the packaging part of [ADR-0007](0007-single-trimmed-exe.md) (where the data lives still stands)

## Context

1.2.0's download was one 35 MB .NET single-file exe that unpacked about 70 MB into `%TEMP%\.net`. The aim is
the smallest single exe that needs nothing installed. Measured on GitHub's runners and locally (13 Oct 2026):

| Build | Download | Needs | Starts |
| --- | --- | --- | --- |
| .NET single-file, trimmed (1.2.0) | 35 MB exe | nothing | yes |
| Native AOT folder, zipped | 28 MB zip, 241 files | nothing | once ported |
| Native AOT folder, 7-Zip (LZMA2 with BCJ2) | 15.5 MB archive | nothing | once ported |
| Native AOT, Windows App Runtime installed separately | 6.4 MB zip | the 46 MB runtime, per PC | once ported |
| Native AOT folder appended to a starter, Brotli (1.3.0) | 20.8 MB exe | nothing | yes |
| **The same, x86 branch filter and LZMA (1.4.0)** | **17.6 MB exe** | **nothing** | **yes** |

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
- **A starter exe** (`Queueify.Launcher`, itself Native AOT, ~1.6 MB) with the published app appended as one
  tar, run through 7-Zip's x86 branch filter (BCJ: call and jump targets made absolute, so calls to one
  function repeat) and compressed with LZMA (64 MB dictionary, longest matches; the LZMA SDK's public-domain
  C# code, in `Queueify.Launcher\Lzma`), and a trailer with the archive's length and a hash id.
  It unpacks once per version into `%LOCALAPPDATA%\Queueify\app\<id>`, starts it with `QUEUEIFY_EXE` set to
  itself, and removes other versions' folders when nothing runs from them. Unpacking:
  - first checks the archive against its id (a SHA-256), before writing anything; LZMA has no checksum, and a damaged
    copy would otherwise install a broken version for good. A mismatch says "download it again".
  - goes into a folder of its own and is renamed into place; a complete folder already there is never
    touched (a second copy started at the same moment may be running from it), and another version's
    unpacking in progress is left alone by the clean-up.
- The app treats `QUEUEIFY_EXE` as the exe the user started: a portable `Data` folder next to the download
  still counts, and the window's taskbar relaunch command points at the download, so a pin survives updates.
- The starter resolves its imports from System32 only (`/DEPENDENTLOADFLAG:0x800`): `bcrypt.dll` isn't a
  KnownDLL, and the download may sit in a folder others can write to.

## Consequences

- One 17.6 MB exe (from 35 MB, and 93 MB in 1.1.0), nothing to install. The first start of a version takes
  about 5 s (unpacking about 70 MB, held in memory with the 64 MB dictionary while it decompresses); later
  starts skip it. The starter shows a small loading screen meanwhile (`LoadingScreen`, plain Win32: icon, name,
  "Getting Queueify ready…"), until the app's window opens; on a start that needs no unpacking, only if the
  window takes over 0.6 s, so it never flashes.
- Building needs Visual Studio's C++ build tools, and a release build takes about 4 minutes (AOT, and a
  minute of LZMA). GitHub's Windows runners have them.
- `crash.log` stack traces name methods but have no file and line (the symbols would be a 50 MB file).
- A new place users may wonder about: `%LOCALAPPDATA%\Queueify\app`, about 70 MB, documented in the README.
- Code that needs run-time reflection or WinRT interop the generator didn't see fails only at run time. The
  UI tests run the download itself (lists, settings, sync, the language restart, reading a pasted photo), and
  `tools\package.ps1` checks the start, the data location, the taskbar pin and the starter's dll loading.
- Self-unpacking exes are sometimes flagged by antivirus heuristics; Windows Defender found nothing.
- Signing the exe (Authenticode) would put the signature after the trailer: `Bundle.Read` would then have to
  find the trailer before the signature.
- The app reads `QUEUEIFY_EXE` once (it must name an existing file) and clears it, so programs it opens
  don't inherit it; a language restart goes back through the starter.

## Alternatives considered

- **Keep .NET single-file (ADR-0007)**: 35 MB, and it unpacks the same amount.
- **Brotli** (1.3.0): built into .NET. For the app's archive (sizes in MiB, like the exe's): Brotli 18.4,
  Brotli with the branch filter 16.8, LZMA alone 17.6, LZMA with the filter 15.9. The filter is what helps,
  and LZMA uses it best; dropping Brotli's native code also made the starter 0.8 MB smaller.
- **7-Zip's BCJ2 filter and LZMA2**: about 1 MB smaller again, but BCJ2 splits the data into four streams
  and needs a much larger decoder.
- Windows' own LZMS (Compression API) came out larger than Brotli (23.1 MB against 18.4 MB for the app).
- **Windows App Runtime installed separately**: a 6.4 MB app, but a 46 MB prerequisite on each PC.
- **Trimming WinUI's other languages**: 0.4 MB, not worth diverging from what the SDK ships.
