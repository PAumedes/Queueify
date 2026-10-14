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
| Clipboard | Leaking data | Written only on explicit "Copy" clicks (sync code, security rules); read only on Ctrl+V or Paste. |
| Network | Exfiltration | Only Spotify, Tidal, the user's own Firebase project and Windows Update. No telemetry. |

Out of scope: a compromised PC or user account, and the security of Spotify, Tidal and Google's services.
