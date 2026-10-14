# ADR-0010: A web app beside the desktop app, one shared queue

- Status: Accepted
- Date: 2026-10-14

## Context

The queue is filled at the PC while reading magazines. Away from home, someone recommends an album and it
has to be added from a phone, then show up on the PCs. Firestore sync ([ADR-0003](0003-firestore-rest-sync.md))
already shares a queue between PCs, but a phone needs an app with a login, and the web version can't keep the
Spotify/Tidal secrets in the browser. The analysis is in [WEB_PORT.md](../WEB_PORT.md).

## Decision

- **`Queueify.Web`**: a Blazor Web App (.NET 10, Interactive Server) with Fluent UI Blazor, ASP.NET Core
  Identity (passkeys, password, optional Google/GitHub) and one SQLite queue per user, stored with
  `BacklogStore`. It references `Queueify.Core`, which now also builds for plain `net10.0`; only
  `OcrService` and `ExternalLinks` are Windows-only.
- **The web server is the shared database.** The desktop app gets a second sync provider next to Firestore,
  speaking the contract below. A PC uses one provider at a time. Firestore stays supported.
- **Phone first for adding**: the Add page is the home page on small screens, installable as a PWA.
- **Secrets stay on the server**: catalogs are searched server-side; the Spotify/Tidal secrets are stored
  encrypted with ASP.NET Data Protection and never sent to the browser.
- **OCR on the web** goes through `IOcr` (Tesseract); Windows OCR stays desktop-only.

## Sync contract (v1)

One round trip: `POST /api/sync` with `Authorization: Bearer <token>`.

Tokens are personal access tokens made in the web app (Settings → Connect a PC), shown once, stored as
SHA-256 hashes, revocable, per user. The text a PC is given is `queueify-web|<https base url>|<token>`.

```jsonc
// request
{ "since": "2026-10-14T12:00:00.000Z" /* the last serverTime received, or null */,
  "albums": [ Album ],        // this PC's dirty rows, deletions included
  "settings": Settings|null } // only when changed here
// response
{ "serverTime": "…Z",         // store as the next "since"
  "albums": [ Album ],        // rows with serverTime > since (not the ones just accepted)
  "settings": Settings|null }
// Album
{ "uid", "artist", "album", "year", "source", "coverUrl", "spotifyId", "tidalId",
  "addedAt", "listenedAt", "rating", "updatedAt", "deleted" }
// Settings: the shared set of AppSettings.CopySharedFrom, plus "updatedAt"
```

Rules: newest `updatedAt` wins per `uid` (ties keep the server's row); the server stamps `serverTime`; text
fields are cut at 500 characters as in the Firestore rules; at most 1,000 albums per request, and the client
repeats until the response is empty; unknown fields are ignored; a bad token is 401 and nothing else is said.
Deletions stay as soft rows. This is the same shape `CloudSync` already uses, so `BacklogStore`'s
`dirty`/`updated_at` columns are enough on the PC.

## Consequences

- Accounts now exist: the first user is the owner and registration can be turned off in configuration.
- The server needs HTTPS and a persistent data folder with backups.
- Two sync providers to keep tested; the contract tests run against the in-process server.
- Desktop and web share copy and string tables, so both languages stay identical.
