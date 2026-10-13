# ADR-0004: Windows' built-in OCR for magazine photos

- Status: Accepted
- Date: 2026-10-11

## Context

Typing artist and album names is the slowest part of the workflow; a photo of the page could replace it.
Cloud vision or AI models read well but cost money per page and need an account.

## Decision

Use `Windows.Media.Ocr`: free, offline, good enough on printed magazine text. The user clicks the lines
for artist and album, so the OCR only has to read, not understand the page. Spanish is preferred (accents,
ñ); other languages can be installed from Settings with `Add-WindowsCapability`, which needs elevation.
Only allow-listed language tags reach the elevated script.

## Consequences

- No cost and no network for reading photos.
- Installing a language needs an administrator's permission and Windows Update; every outcome (declined,
  blocked by policy, interrupted, failed, needs a restart) has its own message and next step.
- A newly installed language may need Queueify to restart before Windows reports it.

## Alternatives considered

- **Cloud OCR or an AI model**: better on hard layouts, but paid per call and online only.
- **Tesseract**: free and offline, but adds tens of MB of models and is less accurate on photos.
