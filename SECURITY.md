# Security

## Reporting a vulnerability

Please **don't** open a public issue. Use GitHub's private vulnerability reporting (Security → Report a
vulnerability) on this repository. You'll get an answer within 7 days, and a fix as soon as it's
verified, with credit if you want it.

## Supported versions

Only the latest release receives fixes.

## Threat model

Queueify runs with the user's rights on their own PC. It has no servers of its own.

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
| Clipboard | Leaking data | Written only on explicit "Copy" clicks (sync code, security rules); read only on Ctrl+V or Paste. |
| Network | Exfiltration | Only Spotify, Tidal, the user's own Firebase project, Windows Update and GitHub (the update check at each start: GitHub sees the address and that Queueify is starting, nothing else; it can be turned off). No telemetry. |

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