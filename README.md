# Queueify

A "listen later" queue for albums recommended in magazines. Type what the magazine says (or click it
on a photo of the page), press Enter, press Enter again. Queueify finds the album on Spotify and Tidal,
saves it to your queue and opens it in the official app when you want to listen. Tick it when you've
heard it, rate it, move on.

The app has its own help: press **F1** (or click **?** in the left bar) for the quick guide, keyboard
shortcuts, troubleshooting and About. A short quick tour runs on the first start and can be replayed
from Help.

## Run

The portable app is in `publish\`. Run `publish\Queueify.exe`. Your queue and settings are stored in
`publish\Data\` (`queueify.db`, `settings.json`, `ui.json`), so copying the folder copies everything.

## One-time setup

**Spotify** (the account that owns the app needs Premium):
1. Go to https://developer.spotify.com/dashboard and choose *Create app*.
2. Use any name. Set Redirect URI to `http://127.0.0.1:8888/callback` (required but unused) and tick *Web API*.
3. Open Queueify → Settings (gear at the bottom of the left bar), expand the Spotify card, paste the
   Client ID and Client secret and click *Save and test*.

**Tidal**:
1. Go to https://developer.tidal.com/dashboard and create an app.
2. Paste the Client ID and Client secret in the Tidal card in Settings and click *Save and test*.

The test runs a real search, so a green "Connected" means the keys work. Settings also has the app that
Play opens, the country for catalog availability (only countries where both services work), the theme
(Windows setting, light or dark) and **Reset layout**, which brings back the Add albums panel, the list
view and the original window size.

**Spanish OCR** (optional, for magazine photos): open *Magazine photo* and click *Install Spanish OCR*.
It needs an admin prompt and internet, once per PC. Without it, OCR still works and matching ignores
accents; only albums with no Spotify/Tidal match keep misread accents.

## Same queue on several PCs (Google Firebase, free)

The queue syncs through your own Google Firebase project on the free Spark plan (no credit card).
A queue of a few thousand albums uses well under 1% of the free daily quota.

**First PC:** click **Sync** in the left bar → *This is my first PC*. The dialog lists the steps:
1. In https://console.firebase.google.com create a project (Analytics not needed).
2. *Build → Firestore Database → Create database*: Standard edition, database ID `(default)`,
   a nearby location (e.g. `southamerica-east1`), production mode.
3. *Rules* tab: paste the rules from **Copy security rules** and click *Publish*.
4. Paste the project ID into Queueify and click **Connect**. Queueify shows a **sync code**.

**Other PCs:** copy the whole Queueify folder (it's already connected), or open **Sync** →
*I already set it up on another PC* and paste the sync code. Albums and the Spotify/Tidal keys come
down automatically.

Changes upload a moment after you make them and download when you switch back to the window. When two
PCs change the same album, the most recent change wins; deletions sync too. The Sync icon shows the
state (point at it for details). Keep the sync code private: anyone who has it can see and change
your queue.

**Rules update (October 2026):** the security rules became stricter (only albums and settings, with
limited field names and sizes). If you published the earlier rules, paste the new ones from
**Copy security rules** and publish again.

**Known limit:** the rules can't stop someone who knows your project ID from creating junk queues
under other keys and using up the free daily quota. They can't read or change *your* queue without
your sync code.

## Using magazine photos

Take the photo with your phone and get it onto the PC (Phone Link, WhatsApp Web, OneDrive…).
Drop it anywhere in the window, paste it with Ctrl+V, or use *Open photo*.
Click the artist line, then the album line: they go into the search box and results appear on the
left. Press Enter to add. The next click after adding starts a new search.

## Keyboard

| Keys | Does |
|---|---|
| Enter (in the search box) | Search; press again to add the highlighted album |
| ↑ / ↓ | Choose another result |
| Esc | Clear the search |
| Ctrl+K or Ctrl+N | Jump to the search box |
| Ctrl+F | Filter the queue |
| Ctrl+B | Hide / show the Add albums panel |
| Ctrl+V | Load a photo from the clipboard |
| F1 | Help |

Double-click an album to play it; right-click for more. Drag the edge of the Add albums panel to resize it.

## Project layout

| Folder | What |
|---|---|
| `Queueify\` | WinUI 3 app: window, navigation, dialogs, quick tour, assets |
| `Queueify.Core\` | Everything that isn't UI: models, matching, OCR, SQLite store, Spotify/Tidal search, Firebase sync, help texts |
| `Queueify.Tests\` | xUnit tests for `Queueify.Core` |
| `tests\ui\` | UI tests driven through Windows UI Automation |
| `tools\` | `firestore.rules` (embedded in the app, tested against the emulator), `make-icon.ps1` |

The logo's master is the vector `Queueify\Assets\Queueify.svg` (used in the app). `tools\make-icon.ps1`
draws the same shapes to produce `Queueify.ico` and the PNGs.

## Build

Requires the .NET 10 SDK.

```powershell
cd Queueify
dotnet publish -c Release -o ..\publish
```

## Running the tests

Changes are made test-first: a failing test, then the code, then all suites green.

```powershell
dotnet test --project Queueify.Tests
```

Firebase sync tests run against the local Firestore emulator and are skipped otherwise. The emulator
needs Java 21+ and the emulator jar (`cloud-firestore-emulator-v1.22.0.jar` from firebase-tools):

```powershell
java -jar cloud-firestore-emulator-v1.22.0.jar --host 127.0.0.1 --port 8080 --rules tools\firestore.rules --project_id demo-queueify
$env:QUEUEIFY_FIRESTORE_EMULATOR = "127.0.0.1:8080"
dotnet test --project Queueify.Tests
```

UI tests (build the app first; they back up and restore the `Data` folder next to the exe):

```powershell
powershell -ExecutionPolicy Bypass -File tests\ui\Queueify.UiTests.ps1 -Exe Queueify\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Queueify.exe
powershell -ExecutionPolicy Bypass -File tests\ui\Queueify.SyncUiTests.ps1 -Exe Queueify\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Queueify.exe
```
