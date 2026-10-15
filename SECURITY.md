# Security

## Reporting a vulnerability

Please **don't** open a public issue. Use GitHub's private vulnerability reporting (Security → Report a
vulnerability) on this repository. You'll get an answer within 7 days, and a fix as soon as it's
verified, with credit if you want it.

## Supported versions

Only the latest release receives fixes.

## Threat model

The desktop app runs with the user's rights on their own PC and has no servers of its own. The optional
web server (`Queueify.Web`, [ADR-0010](docs/adr/0010-web-app-and-shared-queue.md)) is run by the user; its rows are marked below.

| Asset / boundary | Threat | Mitigation |
| --- | --- | --- |
| Spotify and Tidal keys, sync code (`settings.json` in `%LOCALAPPDATA%\Queueify` or the portable `Data` folder) | Someone with access to the folder reads them | Stored unencrypted by design (a portable `Data` folder moves between PCs); documented in the README. The keys only allow catalog search, not account access. |
| The online queue (`queues/{key}` in the user's Firestore) | Others reading or changing it | The 40-hex-character key is the capability. The security rules give no way to list the queues (so keys can't be discovered), allow only albums and settings under a key, and limit their field names and sizes. |
| The user's Firebase quota | Someone who knows the project ID creates junk under other keys | Known limit: the rules can't stop it without Firebase Auth or App Check. Documented in the README. |
| JSON from Spotify, Tidal, Firebase | Malformed or hostile data crashing the app | Parsed defensively; failures become user-facing errors; text sizes are clipped before upload. |
| Elevated OCR install (`Add-WindowsCapability`) | Injection into a script that runs as administrator | Only allow-listed language tags reach it; the log path (from the user's profile) is passed Base64-encoded; tests run the script through PowerShell's parser. |
| Cover addresses (from the services and from other PCs through sync) | A file or network path reading local files or sending the Windows login to another machine | Only `http` and `https` addresses are loaded (`CoverUrls`, tested). |
| The app's unpacked copy (`%LOCALAPPDATA%\Queueify\app`) | Changed files run as Queueify | Only the user (or an admin) can write there, as with any per-user app. The starter is native code and loads its imports from System32 only; the app's working folder is its own. A version's folder is used only once fully unpacked and checked against the SHA-256 in the download. |
| Updates (the exe itself) | A tampered or broken download replacing Queueify | Offered only from Queueify's GitHub releases over HTTPS; the download must match the SHA-256 GitHub lists for it, or it's thrown away and the exe left as it was; it's always written to a new file, never into one someone left next to the exe; installing waits for the user's click ([ADR-0009](docs/adr/0009-update-from-github-releases.md)). Not covered: a release published from a taken-over maintainer account (the exe isn't signed). |
| Personal access tokens (the web server's `/connect` page) | A stolen database or backup yields usable tokens; a token leaks from a PC | Only the SHA-256 hash is stored (the tokens are 256 random bits, so a fast hash is enough), compared in constant time. A token is shown once, can be revoked at any time (a revoked token is refused at once), is bound to one user and is limited to `/api/sync`. At most 20 live tokens per user. Not covered: a token stolen from a PC works until the user revokes it. |
| `POST /api/sync` on the web server | Unauthenticated access; guessing tokens; reading or changing another user's queue; oversized or malformed bodies; a PC with a wrong clock overwriting everything | Bearer tokens only (the login cookie is not accepted, so no CSRF); a bad, missing or revoked token is 401 with an empty body; every attempt counts against a per-address limit (429); the user comes from the token, never from the request, and each user has their own folder and database (ids are checked never to leave the data folder); bodies are capped (413) and read through a cap; at most 1,000 albums per request, text cut at 500 characters, ids limited to `[A-Za-z0-9_-]{1,64}`; an `updatedAt` more than ten minutes ahead of the server is pulled back. Behind a reverse proxy, set `Queueify:Proxy:KnownProxies` (addresses) or `Queueify:Proxy:KnownNetworks` (CIDR): `X-Forwarded-For/Proto` is trusted only from those, otherwise ignored (and then all users share the proxy's address). A user's queue is capped at 50,000 rows (`Queueify:Sync:MaxAlbumsPerUser`; 413 beyond it). A token also reaches the Spotify and Tidal secrets (they sync to the PCs), and tokens keep working while an account is locked out until the user revokes them. Deleted rows are kept for 90 days (they are how other PCs delete) and then purged; a PC offline for longer may keep an album that was deleted elsewhere. |
| Spotify and Tidal secrets on the web server | Someone reads `settings.json` or a backup of a user's folder | The secrets are encrypted with ASP.NET Data Protection (per-user purpose) into `secrets.dat`, never written to `settings.json`, and never sent to a browser; they are sent to the user's own PCs over HTTPS when those sync. Not covered: the Data Protection keys sit in `DataDir\keys` (unencrypted on Linux), so a copy of the whole data folder opens the secrets: back up and protect it as one. |
| Clipboard | Leaking data | Written only on explicit "Copy" clicks (sync code, security rules); read only on Ctrl+V or Paste. |
| Network | Exfiltration | Only Spotify, Tidal, the user's own Firebase project, Windows Update and GitHub (the update check at each start: GitHub sees the address and that Queueify is starting, nothing else; it can be turned off). No telemetry. |

### Web app: uploads and secrets

| Asset / boundary | Threat | Mitigation |
| --- | --- | --- |
| Spotify and Tidal client secrets on the server | Read from disk, a backup, a response or the page | Encrypted in `secrets.dat` with ASP.NET Data Protection, bound to the user, and never written to `settings.json`. Limit, stated plainly: the key ring sits in the data folder (`keys/`, not encrypted itself), so this protects against `settings.json` or a single file leaking, not against someone who copies the whole data folder; keep backups of that folder private, or protect the ring with a certificate or the OS key store when deploying. A lost ring only means re-entering the secrets. The Settings page learns "configured or not", never the value; its secret fields are empty password fields; exports hold no settings; tests assert no response carries a saved secret. |
| Photo upload (`POST /api/photo`) | Decompression bombs, non-images (HTML, SVG), metadata leaks, floods, a stored copy | Signed-in users only; the declared type must be `image/*` (a cross-site form can't send it and no CORS is configured) but the bytes are sniffed (JPEG, PNG, WebP); 12 MB cap on the length and while reading; the pixel count is read from the header (60 MP) before decoding; the result is a fresh PNG without EXIF/ICC/XMP; nothing is written to disk and the clean image's buffer is wiped after reading (the upload's own buffer is only released to the garbage collector); an upload is read into memory only after admission (one photo per user and two per server at a time, the rest get 429); 40 s timeout that kills the child process (the decode itself can't be interrupted, so its slot is kept until it ends). |
| The `tesseract` child process | Argument injection, a hung or crashing engine | Arguments are an array (no shell); the language is one of three fixed choices; the models are checksummed; the process is killed on timeout or cancel and its output is capped. |
| Import (`POST /api/import`) of `queueify.db` or JSON | A crafted SQLite file (views, triggers, huge values, corruption) or oversized JSON | 25 MB cap, one import per user at a time; the file is copied to a private temp name, opened read-only and immutable, with `trusted_schema` off and only a real `items` table accepted (a view, a virtual table or a table with a generated column is refused: generated columns run on every read and a few bytes of file can ask for gigabytes); rows are read with every text cut to 500 characters and every number range-checked, at most 20,000 rows; it is never attached to a live database; what remains is inserted through `BacklogStore`; the copy is deleted afterwards. Cover addresses must be `https`. |
| Export (CSV) | Formula injection when opened in a spreadsheet | Cells starting with `=`, `+`, `-`, `@`, tab or return get a leading apostrophe. |
| Upload and import endpoints | CSRF | Cookies are `SameSite=Lax`; the endpoints take only picture, `application/octet-stream` or `application/json` bodies, which a cross-site form cannot send. |
| Per-user folders | One user reaching another's data | The user id comes from the sign-in claim only and is validated as one plain folder name by `UserStores`. |

Out of scope: a compromised PC or user account, and the security of Spotify, Tidal and Google's services.

## Threat model: the web app (`Queueify.Web`)

| Asset / surface | Threat | Defence |
| --- | --- | --- |
| Sign-in | Guessing passwords | ASP.NET Core Identity with passkeys; failed passwords count towards lockout (5 tries, then locked for 5 minutes). Not covered: a global rate limit in front of the server, so put the site behind a reverse proxy that has one. |
| Registration | A stranger creating an account on a public server | The first account owns the server (checked and written under one lock, so two first sign-ups can't both win); after that registration is closed unless `Queueify:AllowRegistration` is `true`. A fresh public server belongs to whoever registers first: register before exposing it. |
| Cross-site requests | Forged form posts (sign out, language, delete) | Antiforgery tokens on every form post, including `/prefs` and sign out; cookies are `SameSite=Lax`, `HttpOnly`, and `Secure` over HTTPS. |
| Page content | Script injection, clickjacking, tracking pixels | Razor encodes everything; no raw HTML is rendered. Every response carries a Content-Security-Policy (own scripts only, `frame-ancestors 'none'`, `object-src 'none'`, forms only to the site; album covers may come from any HTTPS host and are fetched without a referrer), `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`. |
| Other users' data | Seeing or changing another queue | Each user's queue and settings live in a folder named by their account id (never by anything the browser sends); ids are checked against path tricks. |
| Spotify/Tidal keys | Leaking to the browser, or abuse of the server's address | Keys are read only on the server and never rendered; each user gets 30 catalog searches a minute so one account can't get the server's address blocked. The keys sit unencrypted in the user's `settings.json` on the server's disk: protect the data folder. |
| Redirects | Open redirect after sign-in, sign-out or a language change | Return addresses are reduced to a plain path on this site, otherwise the home page. |
| Service worker | Stale or other users' pages served from cache | It caches only the icons and an offline page, never pages or data, and only for GET requests on this site. |