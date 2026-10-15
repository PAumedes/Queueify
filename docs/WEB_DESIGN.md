# Web design: "liner notes"

The brief is the desktop app: [`MainWindow.xaml`](../Queueify/MainWindow.xaml) with its compact left
navigation, round count badges, 8 px cards and violet accent, and the same promise as UniGetUI: easy to use
as f*ck. The web app must feel like the same product, and must be fastest on the one job a phone does:
somebody says "listen to X", and it is in the queue ten seconds later.

Rejected on purpose: a generic dashboard (cards of statistics, gradient hero, left tree menu), Bootstrap
chrome, and a blue Fluent default. What is kept instead is a small, specific idea: a queue of **record
sleeves on magazine paper**. Warm paper in light, record black in dark, one violet, and covers as the only
decoration (an album with no cover gets a deterministic tinted sleeve with its initials, so a list of
albums typed by hand still looks intentional).

## Palette (6 names, defined once in `wwwroot/queueify.css`)

| Name | Light | Dark | Used for |
| --- | --- | --- | --- |
| Paper | `#F7F4EF` | `#14111B` | page background (the dark one is the logo's record black) |
| Sleeve | `#FFFFFF` | `#1E1A28` | cards, inputs, the rail |
| Ink | `#1B1724` | `#F2EFF8` | text; muted `#665F75` / `#A9A3B8` (>= 4.5:1 on Paper and Sleeve) |
| Violet | `#6544E6` fill, `#5030C4` text | `#6B4CF0` fill, `#B5A2FF` text | the accent: primary buttons, current page, focus ring (the desktop's `#7B5CFF` family) |
| Rose | `#D6336C` | `#E8467F` | the round count badges and rating stars, nothing else |
| Hairline | `#E6E1D8` | `#2E2940` | borders and dividers |

Colour never carries meaning alone: state is also text or an icon (Listened has a check and the word,
"In your queue" is a label, errors say "Error").

## Type

System UI stack with Segoe UI Variable first, as the desktop app: `"Segoe UI Variable Text", "Segoe UI",
system-ui, sans-serif`. Roles: **Display** 28/34 semibold with slight negative tracking (page title),
**Title** 16/22 semibold (album), **Body** 14/20, **Meta** 12.5/18 muted (artist line, year, magazine,
dates), **Badge** 12 semibold with tabular numerals. Only two weights (400, 600).

## Layout

Phone (the primary device for adding), under 760 px:

```
+--------------------------------+
| Add albums                  EN |   title row, language/theme in "More"
|--------------------------------|
| [magazine: Pelo            v]  |   remembered from the last add
| [ Artist - Album 1999     (Q)] |   one big search box, autofocus
|                                |
| +----+ Clics Modernos          |
| |cov | Charly Garcia - 1983    |   result: cover, title, artist, year
| +----+ [Spotify][Tidal] Best   |   pills, "Best match" label
|        [      Add       ]      |   one big button, 48 px
| ...                            |
|--------------------------------|
| (1)Queue  (+)Add  Listened  ...|   bottom tab bar: the 4 pages you use
| [12]       big      [40]   More|   + More (Help, Settings, Connect, Sign out)
+--------------------------------+
```

Desktop, 760 px and wider (the same destinations as the WinUI `NavigationView`, left compact):

```
+------+---------------------------------------------------------------+
| logo | (queue icon) To listen                                        |
|      |  12 albums waiting - 2 added this week                        |
| (12) |  [Filter (Ctrl+F)        ] [Order v] [x] Covers  [Surprise me] |
| Queue|  +----------------------------------------------------------+ |
|      |  | cover  Album                       [> Tidal] (o) (...)   | |
| Add  |  |        Artist                                            | |
|      |  |        1983 - Pelo - added 3 Oct                         | |
| (40) |  +----------------------------------------------------------+ |
| Done |  ...                                                          |
| Photo|                                                               |
| Help |                                                               |
| Sett.|                                                               |
| Conn.|                                                               |
|  EN  |                                                               |
| Exit |                                                               |
+------+---------------------------------------------------------------+
```

## Behaviour

- The rail and the tab bar are one component listing **every** page (Queue, Add, Listened, Photo, Help,
  Settings, Connect, Sign out), so tracks that add pages only fill the routes.
- Round badges are the number of albums waiting and listened; they update when an album moves.
- "Mark as listened" shows a note at the bottom with Undo and five stars, so rating takes one tap and
  can be skipped. Delete also offers Undo. Nothing asks "are you sure".
- Play opens the installed app (`spotify:` / `tidal:`) and falls back to the web player after a second.
- Theme (system, light, dark) and language (English, Rioplatense Spanish) are cookies set by a plain form
  POST to `/prefs`, so they apply to the first paint and work without scripts. The language otherwise
  follows the browser's.
- Keyboard: `Ctrl+F` filter (or the search box on Add), `Ctrl+K` Add, `F1` Help, `Esc` clears the box.

## Floor (checked in review)

Responsive from 320 px; visible `:focus-visible` ring on everything; `prefers-reduced-motion` removes all
motion; text contrast 4.5:1 or more; every icon-only button has an `aria-label` and a `title`; touch
targets are 44 px or larger on phones; an `Escape`/keyboard path exists for every pointer action; a skip
link jumps to the main content.

## Why not more Fluent components

Fluent UI Blazor v5 is used for buttons, the switch, icons and the counter semantics. The text inputs and
the forms of the Identity pages are native elements styled with the same tokens, because those pages are
static server-rendered forms (no circuit), where a native `<input name=...>` is the only thing that posts
reliably, with or without scripts, and what password managers and passkeys understand.
