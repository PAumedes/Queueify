# ADR-0001: A portable, unpackaged WinUI 3 app

- Status: Accepted (how it is packaged: see ADR-0007)
- Date: 2026-10-11

## Context

Queueify is used on a few of the owner's PCs, at the desk where the magazines are read. It must be
quick to set up on another PC, look like a current Windows app, and use Windows features (OCR, file
pickers, drag and drop). The Microsoft Store isn't a goal.

## Decision

A WinUI 3 app on the Windows App SDK and .NET, published **unpackaged and self-contained** for win-x64.
Everything the app keeps lives in a `Data` folder next to the exe, so copying the folder moves the queue,
the keys and the sync connection.

## Consequences

- No installer, no admin rights, no runtime to install. A release was a ~90 MB zip; since ADR-0007 it is
  one ~35 MB exe.
- The app isn't signed, so SmartScreen warns on first run.
- Unpackaged publishing misses the app's own `.xbf` and `.pri` files (since ADR-0007, `EnableMsixTooling`
  includes them).
- Features that need package identity (some notifications, the Store) are out of reach.

## Alternatives considered

- **MSIX package**: clean install and updates, but needs signing or developer mode, and loses the
  copy-the-folder portability.
- **WPF or WinForms**: mature, but don't look like Windows 11 without a lot of styling.
- **A web app**: no access to Windows OCR and drag and drop of local files; needs hosting.
