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

### Web app: uploads and secrets

| Asset / boundary | Threat | Mitigation |
| --- | --- | --- |
| Spotify and Tidal client secrets on the server | Read from disk, a backup, a response or the page | Encrypted in `settings.json` with ASP.NET Data Protection (the key ring is in the data folder: back it up with the data and keep it private; a lost ring only means re-entering the secrets). The Settings page learns "configured or not", never the value; its secret fields are empty password fields; exports hold no settings; tests assert no response carries a saved secret. |
| Photo upload (`POST /api/photo`) | Decompression bombs, non-images (HTML, SVG), metadata leaks, floods, a stored copy | Signed-in users only; the declared type must be `image/*` (a cross-site form can't send it and no CORS is configured) but the bytes are sniffed (JPEG, PNG, WebP); 12 MB cap on the length and while reading; the pixel count is read from the header (60 MP) before decoding; the result is a fresh PNG without EXIF/ICC/XMP; nothing is written to disk and the clean image's buffer is wiped after reading (the upload's own buffer is only released to the garbage collector); an upload is read into memory only after admission (one photo per user and two per server at a time, the rest get 429); 40 s timeout that kills the child process (the decode itself can't be interrupted, so its slot is kept until it ends). |
| The `tesseract` child process | Argument injection, a hung or crashing engine | Arguments are an array (no shell); the language is one of three fixed choices; the models are checksummed; the process is killed on timeout or cancel and its output is capped. |
| Import (`POST /api/import`) of `queueify.db` or JSON | A crafted SQLite file (views, triggers, huge values, corruption) or oversized JSON | 25 MB cap, one import per user at a time; the file is copied to a private temp name, opened read-only and immutable, with `trusted_schema` off and only a real `items` table accepted (a view, a virtual table or a table with a generated column is refused: generated columns run on every read and a few bytes of file can ask for gigabytes); rows are read with every text cut to 500 characters and every number range-checked, at most 20,000 rows; it is never attached to a live database; what remains is inserted through `BacklogStore`; the copy is deleted afterwards. Cover addresses must be `https`. |
| Export (CSV) | Formula injection when opened in a spreadsheet | Cells starting with `=`, `+`, `-`, `@`, tab or return get a leading apostrophe. |
| Upload and import endpoints | CSRF | Cookies are `SameSite=Lax`; the endpoints take only picture, `application/octet-stream` or `application/json` bodies, which a cross-site form cannot send. |
| Per-user folders | One user reaching another's data | The user id comes from the sign-in claim only and is validated as one plain folder name by `UserStores`. |

Out of scope: a compromised PC or user account, and the security of Spotify, Tidal and Google's services.
