# ADR-0011: Tesseract, run as a child process, reads photos on the web

- Status: Accepted
- Date: 2026-10-14

## Context

The web app can't use Windows OCR ([ADR-0004](0004-windows-ocr.md)): the server may run Linux, and users send
photos from phones. It needs free, offline text recognition for Spanish and English that works on Windows and
Linux x64 hosts, with photo uploads treated as untrusted input ([ADR-0010](0010-web-app-and-shared-queue.md)).

## Decision

- **Tesseract 5 with the `spa` and `eng` models**, behind `IOcr` (Core, both target frameworks). The desktop
  keeps `WindowsOcr`, an adapter over the existing `OcrService`. `OcrService.CleanLine` (year and
  heading cleanup) moved to a file both builds compile.
- **The `tesseract` program, started as a child process**, not an in-process NuGet binding. The picture goes
  in through stdin and TSV (one row per word, with boxes) comes out of stdout, so nothing is written to
  disk. Why not `Tesseract` (charlesw) or `TesseractOCR` (Sicos1977): both ship native libraries for Windows only,
  so Linux would need hand-built `.so` files and exact symlinks. The program is one `apt install
  tesseract-ocr` on Linux (or an installer on Windows), a hung or crashing engine can be killed on a timeout
  without touching the web process, and the TSV parser is plain tested code. `Ocr:TesseractPath` points at it.
- **Models**: downloaded once from `tessdata_fast` at a pinned release (4.1.0) into `<data>/tessdata`, accepted only when the
  SHA-256 matches the one in the code, replaced when a local copy doesn't match. An administrator who sets
  `Ocr:TessdataPath` takes over: nothing is downloaded or checked. The fast models are 2 MB (`spa`) and
  4 MB (`eng`); the "best" models read photos slightly better and could be swapped in by that setting.
- **Image handling with ImageSharp** (Six Labors Split License: Apache-2.0 for software under an open source
  license, which this GPL project is). An upload is sniffed by its bytes (JPEG, PNG or WebP; the declared type
  is only a CSRF gate), its pixel count is read from the header before decoding (60 MP), it is turned upright,
  scaled to 3000 px and re-encoded as PNG, which drops EXIF (GPS), ICC and XMP. The upload and the PNG exist only
  in memory and are wiped after reading.
- **Limits**: 12 MB per upload (checked on the length and again while reading), 40 s per photo, one photo at a
  time per user and two across the server (the rest get 429), at most 300 lines of 300 characters back.
- **Languages** offered are `auto` (Spanish + English), `es` and `en`; only those reach the command line.

## Consequences

- Deploying needs the `tesseract` program (the Dockerfile installs it); without it Photo says the reader isn't
  available, and the rest of the app works.
- Quality on real magazine photos is below Windows OCR on hard layouts; the user taps the lines they want, so
  the engine only has to read. A cloud reader can be added behind `IOcr` later.
- Tests: logic against a fake `IOcr`; a golden-image test on the desktop's magazine page runs only when
  `QUEUEIFY_TESSERACT` and `QUEUEIFY_TESSDATA` point at a program and models (it is skipped otherwise).

## Alternatives considered

- **In-process bindings**: faster per call, but native-library handling per OS, and a crash takes the server down.
- **Azure AI Vision / Google Vision**: better quality, paid and online; photos would leave the server.
- **Browser-side `tesseract.js`**: no server work, but a large download per phone and slow on low-end devices.
