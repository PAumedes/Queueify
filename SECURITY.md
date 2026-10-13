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
| Spotify and Tidal keys, sync code (`Data\settings.json`) | Someone with access to the folder reads them | Stored unencrypted by design (the folder is portable between PCs); documented in the README. The keys only allow catalog search, not account access. |
| The online queue (`queues/{key}` in the user's Firestore) | Others reading or changing it | The 40-hex-character key is the capability. The security rules give no way to list the queues (so keys can't be discovered), allow only albums and settings under a key, and limit their field names and sizes. |
| The user's Firebase quota | Someone who knows the project ID creates junk under other keys | Known limit: the rules can't stop it without Firebase Auth or App Check. Documented in the README. |
| JSON from Spotify, Tidal, Firebase | Malformed or hostile data crashing the app | Parsed defensively; failures become user-facing errors; text sizes are clipped before upload. |
| Elevated OCR install (`Add-WindowsCapability`) | Injection into a script that runs as administrator | Only allow-listed language tags reach it; the log path (from the user's profile) is passed Base64-encoded; tests run the script through PowerShell's parser. |
| Clipboard | Leaking data | Written only on explicit "Copy" clicks (sync code, security rules); read only on Ctrl+V or Paste. |
| Network | Exfiltration | Only Spotify, Tidal, the user's own Firebase project and Windows Update. No telemetry. |

Out of scope: a compromised PC or user account, and the security of Spotify, Tidal and Google's services.
