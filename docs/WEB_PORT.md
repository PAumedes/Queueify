# Web version: analysis and report

Status: proposal (2026-10-14). Nothing here is built yet.

## 1. Summary

Port Queueify to the web as a **Blazor Web App on .NET 10** that references `Queueify.Core` directly.
About 2,500 lines of Core carry over almost untouched; the ~3,200 lines of WinUI code (`MainWindow.xaml`
and its code-behind) are rewritten as Razor components, with the same layout, copy and Fluent look.

Why this needs the least new code:

| Choice | Reason |
| --- | --- |
| Blazor (C# in components) | The XAML page structure maps one-to-one to Razor components; ViewModel-style logic in `MainWindow.xaml.cs` is plain C# and moves over. |
| Reference `Queueify.Core` as is | Matcher, catalogs, `BacklogStore`, `Loc` + `Strings.En/Es`, `Countries`, `QueueText`, `Shortcuts` have no UI or Windows dependency. All 20 test files keep working. |
| Fluent UI Blazor (`Microsoft.FluentUI.AspNetCore.Components`) | Same Fluent design tokens, accent, corner radius, light/dark, nav pane, cards, dialogs, toasts and info bars as WinUI 3. Closest possible aesthetic with no custom CSS framework. |
| Interactive Server render mode | No API layer to write: components call services directly and secrets never reach the browser. Use WebAssembly only if offline use is wanted (see §6). |

## 2. What carries over from the current code

| Part | Lines | Web fate |
| --- | --- | --- |
| `Models/*` (BacklogItem, AlbumResult, AlbumMatch, AppSettings) | ~270 | Reuse as is. |
| `Matcher`, `SpotifyCatalog`, `TidalCatalog`, `CoverUrls`, `Countries` | ~600 | Reuse as is (server-side `HttpClient`; fixes the browser CORS problem too). |
| `Loc`, `Strings.En`, `Strings.Es`, `QueueText`, `Shortcuts` | ~850 | Reuse. `Loc` is a static, so it must become per-request/per-user (see §4, localization). |
| `BacklogStore` (SQLite) | 259 | Reuse, one database file per user (or an EF Core migration later). |
| `Updater`, `AppData`, `UiState`, `ExternalLinks` | ~490 | Drop or replace (web has no self-update; app data is server config; `UiState` becomes user preferences). |
| `CloudSync` + Firestore rules | ~310 | Not needed for the web: the server is the single source of truth. Keep it only as an **import/sync bridge** with the desktop app (see §5). |
| `OcrService`, `OcrLanguages` | ~350 | Replace (Windows.Media.Ocr is Windows-only; see §4). |
| `Queueify.Launcher`, `tools/Pack`, AOT/LZMA packaging | ~3,500 | Not used by the web. |
| `MainWindow.xaml` + `.cs`, `Controls/*` | ~3,400 | Rewrite as Razor components (see §3). |

Core currently targets `net10.0-windows10.0.26100.0`. Only `OcrService` (and `ExternalLinks`' `Process.Start`)
need Windows. Move those two behind interfaces (`IOcr`, `ILinkOpener`), retarget Core to plain `net10.0`
(or multi-target both), and the web project can reference it. The desktop app keeps using the Windows
implementations. This is the single biggest "less code" lever.

## 3. UI mapping (WinUI to Blazor)

| WinUI 3 | Blazor / Fluent UI |
| --- | --- |
| `NavigationView` (LeftCompact) with InfoBadges | `FluentNav` / `FluentNavMenu` with `FluentBadge`; collapses to an icon rail. |
| Left "Add" pane + `PaneSplitter` | CSS grid with a resizable column (small JS-free splitter or `FluentSplitter`). |
| `ListView` / `GridView` for queue (rows / cover tiles) | `FluentDataGrid` or a plain `@foreach` list with the same templates; virtualise with `<Virtualize>`. |
| `CommandBar` (filter, Surprise me, sort, covers toggle) | `FluentToolbar` + `FluentSearch`, `FluentMenuButton`, `FluentSwitch`. |
| `InfoBar`, toast | `FluentMessageBar`, `IToastService`. |
| `ContentDialog`, `TeachingTip` quick tour | `IDialogService`, `FluentPopover` (tour steps). |
| `ServiceCard`, `ServiceLogo`, round number badges, pills | Small Razor components; the `Card`, `Pill`, `StepNumber` styles become ~40 lines of CSS using Fluent tokens (`--neutral-fill-rest`, `--accent-fill-rest`, `--layer-corner-radius`). |
| `ComboBox`, `ToggleSwitch` | `FluentSelect`, `FluentSwitch`. |
| Keyboard accelerators (Ctrl+F, Ctrl+B, Ctrl+V, F1) | Small JS interop keydown handler (or `@onkeydown` on the root). |
| Drag and drop photo, clipboard paste image | `InputFile` + a small JS interop for paste/drop. |
| Photo page with zoom and OCR line boxes | `<img>` + an SVG/canvas overlay; zoom by CSS transform. |
| Light/dark/system theme | Fluent design token `BaseLayerLuminance` + `prefers-color-scheme`. |

Aesthetic notes: keep Segoe UI Variable where available with `system-ui` fallback, 8 px corner radius,
the same accent colour, same icons (Fluent System Icons, same family as the `Segoe Fluent Icons` glyphs),
and the existing copy (the strings carry over verbatim).

## 4. New components and services required

### Identity (login / logout)
The desktop app has no accounts; the queue is the "password" (the Firestore key). A web app that holds
secrets needs real accounts.

| Need | Recommendation |
| --- | --- |
| Sign in / sign out | **ASP.NET Core Identity** (Blazor Web App template, "Individual accounts"). .NET 10 adds **passkey** support, so a passwordless login is built in. |
| Social login (fewer passwords to manage) | Add Google and/or GitHub external providers (`AddGoogle`, `AddGitHub`). |
| Pages | `Login`, `Logout`, `Register`, `ForgotPassword`/`ResetPassword` (only if passwords are used), `Manage` (change email, passkeys, delete account). Scaffolded by the template; restyled with Fluent. |
| Authorisation | `[Authorize]` on every page except Login/Register/Help. Single-user mode option: register disabled after the first user (`AllowRegistration=false`). |
| Per-user data | The user id picks the database: `data/{userId}/queueify.db`. |
| Anti-forgery, cookies | Defaults of the template (HTTPS-only, `HttpOnly`, `SameSite=Lax`). |

### Server-side services
| Component | Notes |
| --- | --- |
| `IUserDataLocator` | Replaces static `AppData.Folder` with a per-user folder. |
| Settings store (per user) | Replaces `settings.json`. Spotify/Tidal client secrets **encrypted with ASP.NET Data Protection**, never sent to the browser. |
| `IOcr` + implementation | Windows OCR does not exist on Linux hosts. Options: **Tesseract** (NuGet `Tesseract`, `spa`/`eng` traineddata, free, runs anywhere); **Azure AI Vision Read** (best quality for magazine photos, small cost); or host on Windows and keep `Windows.Media.Ocr`. Recommended: Tesseract first, Azure behind the same interface later. |
| `ILinkOpener` | Web: plain `<a href="spotify:..." target="_blank">`; deep links `https://open.spotify.com/...` and `https://tidal.com/browse/album/...` work in browsers. |
| Per-request localization | `Loc` is static today. Wrap in a scoped `ILocalizer` (same string tables) keyed on the user's language; keeps Rioplatense ES. |
| Photo upload limits | Max size, content-type check, `ImageSharp`-based re-encode, delete after OCR (nothing stored). |
| Backup / export | Download `queueify.db` as JSON/CSV; **import** from desktop (see §5). |
| Health + logging | `/healthz`, structured logs, no secrets in logs. |

Not needed on the web: updater, launcher/packaging, taskbar identity, PaneSplitter drag code, Firestore
sync (optional), `OcrLanguages` installer.

## 5. Moving data and keeping both apps

Two options:

1. **Web replaces the desktop app.** One-time import of `queueify.db` (upload the file, rows keep their
   `uid`, so nothing duplicates). Simplest.
2. **Both live together.** The server exposes a small sync endpoint in the **same shape `CloudSync`
   already speaks** (`uid`, `updatedAt`, `deleted`, server time). The desktop app gets a second "provider"
   next to Firestore. More code; do this only if the desktop app stays.

Recommendation: option 1 first, option 2 only if wanted.

## 6. Hosting and rendering decision

| Option | Pros | Cons |
| --- | --- | --- |
| Blazor Server (recommended) | Least code, secrets stay on server, Core runs unchanged. | Needs a live connection; no offline. |
| Blazor WebAssembly + minimal API | Offline/PWA possible. | Needs an API layer, SQLite in browser or sync, secrets and catalog calls must still be proxied (CORS, client secret). More code. |
| Host | Azure App Service / Container Apps (free-tier friendly), or a Docker container on the existing machine. SQLite on a mounted volume + nightly copy. | |

## 7. Plan (test first, per project rules)

1. Introduce `IOcr` / `ILinkOpener` in Core; retarget Core to `net10.0`; tests stay green.
2. New `Queueify.Web` project (Blazor Web App, Identity template, Fluent UI). Add a bUnit + xUnit project.
3. Shell: layout, nav, theme, localization, login/logout (tests: unauthenticated redirect, per-user data isolation).
4. Queue page: list/cover view, filter, sort, Surprise me, mark listened, rating.
5. Add pane: search with Matcher + catalogs, add without link.
6. Photo page with Tesseract.
7. Settings page (keys encrypted, country, preferred service, theme, language) + Help + quick tour.
8. Import/export, hosting, CI, security review ([SECURITY.md](../SECURITY.md) updated), docs and an ADR.

Rough size: 25-35 Razor components, ~2,000 lines of new C# and ~400 lines of CSS, against ~3,400 lines of
WinUI code retired for the web. Core and its tests are unchanged except the two interface extractions.

## 8. Risks

- Client secrets for Spotify/Tidal now live on a server: encrypt, restrict file permissions, rate-limit search.
- Photo OCR quality differs from Windows OCR; compare on real magazine photos before choosing Tesseract vs Azure.
- Blazor Server keeps a circuit per tab; fine for a few users, plan capacity if opened to many.
- Static `Loc` and `AppData` are the main hidden global state to remove.

## 9. Tooling to use on this work (saved for later)

- `/security-review` (built into Claude Code) on every PR; optional GitHub Action
  `anthropics/claude-code-security-review@main` with secret `CLAUDE_API_KEY`
  (only for trusted PRs; not hardened against prompt injection).
  Copy `.claude/commands/security-review.md` from that repo to customise rules
  (add: "secrets in Data Protection", "per-user data isolation", "upload handling").
- Anthropic `frontend-design` skill (github.com/anthropics/skills, `skills/frontend-design/SKILL.md`)
  for the web UI. Its brief is "distinctive, not templated", so give it the existing Fluent/UniGetUI direction
  as the explicit visual brief ("follow the brief exactly"). Process: design plan, review, build,
  critique with screenshots. Floor: responsive, visible focus, reduced motion, contrast.
- Official .NET skills: `dotnet/skills` marketplace.
  Claude Code: `/plugin marketplace add dotnet/skills`, then `/plugin marketplace browse dotnet-agent-skills`
  and `/plugin install <plugin>@dotnet-agent-skills` (for example `dotnet-diag`, skill
  `analyzing-dotnet-performance`). Skills are plain `SKILL.md` files, so they can also be copied by hand.
