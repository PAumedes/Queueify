# UI tests for Queueify, driven through Windows UI Automation.
#
#   powershell -ExecutionPolicy Bypass -File tests\ui\Queueify.UiTests.ps1 -Exe <path to Queueify.exe>
#
# Starts the app with an empty Data folder, so it behaves like a first run (no API keys, no sync).
# Prints PASS/FAIL per check and exits with the number of failures.
param([Parameter(Mandatory)] [string]$Exe)
$Exe = (Resolve-Path $Exe).Path

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
$A = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]
$Cond = [System.Windows.Automation.Condition]
$script:failures = 0

function Check([bool]$ok, [string]$what) {
    if (-not $ok) { $script:failures++ }
    '{0}  {1}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what
}

function Start-App {
    $p = Start-Process $Exe -PassThru
    for ($i = 0; $i -lt 60 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
    Start-Sleep 3
    $script:proc = $p
    $script:root = $A::FromHandle($p.MainWindowHandle)
}

function Stop-App {
    $script:proc.CloseMainWindow() | Out-Null
    if (-not $script:proc.WaitForExit(10000)) { $script:proc.Kill(); $script:proc.WaitForExit(10000) | Out-Null }
}

# Only the copy under test is stopped; a Queueify running from another folder is left alone.
function Stop-TestApp {
    Get-Process Queueify -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } |
        ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }
}

function All { $script:root.FindAll($Tree::Descendants, $Cond::TrueCondition) }
function Find([string]$name, [string]$type = '*') {
    All | Where-Object { $_.Current.Name -like $name -and $_.Current.ControlType.ProgrammaticName -like "ControlType.$type" } | Select-Object -First 1
}
function FindId([string]$id) { All | Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1 }
function Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 800 }
function Select-Item($el) { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 800 }
function SetText($el, [string]$text) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function Keys([string]$keys) {
    # The window itself can't take UI Automation focus (its children can); keys go to the foreground window anyway.
    try { $script:root.SetFocus() } catch { }
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds 800
}

# Fresh start: the Data files next to the exe are moved to a backup folder first and put back at
# the end, so running the tests against a real copy never loses a queue, keys or sync settings.
$data = Join-Path (Split-Path $Exe) 'Data'
$files = 'queueify.db', 'settings.json', 'ui.json', 'crash.log'
$backup = Join-Path ([System.IO.Path]::GetTempPath()) ("queueify-uitest-backup-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $backup | Out-Null
Stop-TestApp

# Puts every original file back. The backup folder is only deleted when all of them made it;
# otherwise it stays and its path is printed, so the user's data is never lost.
function Restore-Data {
    $allBack = $true
    foreach ($f in $files) {
        $path = Join-Path $data $f
        $saved = Join-Path $backup $f
        try {
            if (Test-Path $saved) { Move-Item $saved $path -Force -ErrorAction Stop }
            elseif (Test-Path $path) { Remove-Item $path -ErrorAction Stop }
        }
        catch {
            $allBack = $false
            "WARNING: couldn't restore $f ($_). The original is still in $backup"
        }
    }
    if ($allBack) { Remove-Item $backup -Recurse -ErrorAction SilentlyContinue }
}

# Whatever happens below (a failed step, Ctrl+C), the app is closed and the real data put back.
# The backup itself happens inside, so even a half-done backup is restored.
try {

foreach ($f in $files) {
    $path = Join-Path $data $f
    if (Test-Path $path) { Move-Item $path (Join-Path $backup $f) -ErrorAction Stop }
}

# ── Closing the app during the tour must not bring the tour back ──
Start-App
Check ($null -ne (Find '*1 of 4*')) 'quick tour starts on a first run'
Stop-App
Start-App
Check ($null -eq (Find '*of 4*')) 'quick tour does not come back after closing the app mid-tour'
Stop-App
Remove-Item (Join-Path $data 'ui.json')

Start-App

# ── First run: quick tour ──
Check ($null -ne (Find '*1 of 4*')) 'quick tour starts on first run (step 1 of 4)'
foreach ($step in 2..4) {
    Invoke (Find 'Next' 'Button')
    Check ($null -ne (Find "*$step of 4*")) "quick tour step $step of 4"
}
Invoke (Find 'Done' 'Button')
Check ($null -eq (Find '*of 4*')) 'quick tour closes with Done'

# ── Navigation and page headers ──
foreach ($item in 'To listen', 'Listened', 'Magazine photo', 'Help', 'Settings') {
    Check ($null -ne (Find "$item*" 'ListItem')) "navigation item: $item"
}
$sync = Find 'Sync*' 'ListItem'
Check ($null -ne $sync) "navigation item: Sync ($($sync.Current.Name))"
Check ($null -ne (FindId 'PageTitle') -and (FindId 'PageTitle').Current.Name -eq 'To listen') 'page title shows "To listen"'
Check ((FindId 'PageSummary').Current.Name -like 'Nothing waiting*') "page summary: $((FindId 'PageSummary').Current.Name)"

# ── Add albums panel: both fields titled alike, the magazine field explains itself ──
$magazineTitle = Find 'Magazine' 'Text'
$searchTitle = Find 'Find an album' 'Text'
Check ($null -ne $magazineTitle -and $null -ne $searchTitle -and
       $magazineTitle.Current.BoundingRectangle.Height -eq $searchTitle.Current.BoundingRectangle.Height) `
    "field titles have the same size ($($magazineTitle.Current.BoundingRectangle.Height) vs $($searchTitle.Current.BoundingRectangle.Height))"
Check ($null -ne (Find 'Optional. Saved with every album*' 'Text')) 'the magazine field says what it is for'

# ── Every button says what it does ──
$unnamed = All | Where-Object {
    $_.Current.ControlType.ProgrammaticName -in 'ControlType.Button', 'ControlType.SplitButton' -and
    -not $_.Current.IsOffscreen -and [string]::IsNullOrWhiteSpace($_.Current.Name)
}
Check (@($unnamed).Count -eq 0) "all visible buttons have names ($(@($unnamed).Count) unnamed: $((@($unnamed) | ForEach-Object { $_.Current.AutomationId }) -join ', '))"

# ── Add an album (no API keys yet: add without a link) ──
SetText (Find 'Find an album' 'Edit') 'Charly Garcia - Clics Modernos 1983'
Start-Sleep 2
$addLink = Find 'Add *without a link' 'Hyperlink'
Check ($null -ne $addLink) 'offers to add without a link when no service is connected'
Invoke $addLink
Check ((Find 'To listen*' 'ListItem').Current.Name -like '*1*') "badge counts 1 album: $((Find 'To listen*' 'ListItem').Current.Name)"
Check ($null -ne (Find 'Clics Modernos' 'Text')) 'album shows in the queue'
Check ((FindId 'PageSummary').Current.Name -like '1 album waiting*') "summary updates: $((FindId 'PageSummary').Current.Name)"
Check ($null -ne (Find 'Added *Clics Modernos*')) 'notification with Undo after adding'

# ── Listened + undo ──
Invoke (Find 'Mark as listened' 'Button')
Check ((Find 'Listened*' 'ListItem').Current.Name -like '*1*') "listened badge: $((Find 'Listened*' 'ListItem').Current.Name)"
Invoke (Find 'Undo' 'Button')
Check ((Find 'To listen*' 'ListItem').Current.Name -like '*1*' -and (Find 'Listened*' 'ListItem').Current.Name -eq 'Listened') `
    "undo moves it back: $((Find 'To listen*' 'ListItem').Current.Name) / $((Find 'Listened*' 'ListItem').Current.Name)"

# ── Views and panel ──
$covers = Find 'Cover view*'
$covers.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
Start-Sleep 1
Check ($null -ne (Find 'Clics Modernos' 'Text')) 'cover view shows the album'
Invoke (Find 'Hide this panel*' 'Button')
$rail = Find 'Show the Add albums panel*' 'Button'
Check ($null -eq (Find 'Find an album' 'Edit') -and $null -eq (Find 'Magazine' 'Edit') -and $null -ne $rail) `
    'add panel hides and a rail offers it back'
# The way back sits where the panel was (left of the page), not somewhere else on the screen.
Check ($null -ne $rail -and $rail.Current.BoundingRectangle.Right -lt (FindId 'PageTitle').Current.BoundingRectangle.Left) `
    'the rail is on the left, where the panel was'

# ── Photo page ──
Select-Item (Find 'Magazine photo*' 'ListItem')
Check ((FindId 'PageTitle').Current.Name -eq 'Magazine photo') 'photo page title'
Check ($null -ne (Find 'Open photo*' 'Button')) 'photo page offers Open photo'

# ── Help (F1) ──
Keys '{F1}'
Check ((FindId 'PageTitle').Current.Name -eq 'Help') 'F1 opens Help'
Check ($null -eq (Find 'Show the Add albums panel*' 'Button')) 'Help uses the full width: no rail'
foreach ($section in 'Quick guide', 'Keyboard shortcuts', 'Troubleshooting', 'About Queueify') {
    Check ($null -ne (Find $section 'Text')) "help section: $section"
}
Check ($null -ne (Find 'Version 1.*' 'Text')) "about shows the version: $((Find 'Version*' 'Text').Current.Name)"
Check ($null -ne (Find 'Replay the quick tour' 'Button')) 'help can replay the tour'

Stop-App

# ── Second start: no tour, layout remembered ──
Start-App
Check ($null -eq (Find '*1 of 4*')) 'quick tour does not repeat on the next start'
Check ($null -ne (Find 'Clics Modernos' 'Text')) 'queue persisted'
$coverState = (Find 'Cover view*').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
Check ($coverState -eq 'On') "cover view remembered ($coverState)"
Check ($null -eq (Find 'Find an album' 'Edit') -and $null -ne (Find 'Show the Add albums panel*' 'Button')) `
    'a hidden panel stays hidden after a restart, with the rail to bring it back'
Invoke (Find 'Show the Add albums panel*' 'Button')
Check ($null -ne (Find 'Find an album' 'Edit') -and $null -eq (Find 'Show the Add albums panel*' 'Button')) 'the rail brings the panel back'
Keys '^b'
$rail = Find 'Show the Add albums panel*' 'Button'
Check ($null -ne $rail -and $rail.Current.HasKeyboardFocus) 'Ctrl+B hides the panel and leaves keyboard focus on the rail'
Keys '^b'
Check ($null -ne (Find 'Find an album' 'Edit') -and (Find 'Find an album' 'Edit').Current.HasKeyboardFocus) 'Ctrl+B again brings it back, ready to type'

# ── Settings page ──
Select-Item (Find 'Settings*' 'ListItem')
Check ((FindId 'PageTitle').Current.Name -eq 'Settings') 'Settings opens as a page'
Check ($null -eq (Find 'Find an album' 'Edit') -and $null -eq (Find 'Show the Add albums panel*' 'Button')) 'Settings uses the full width'
foreach ($section in 'Music services', 'Appearance', 'Sync between your PCs') {
    Check ($null -ne (Find $section 'Text')) "settings section: $section"
}
foreach ($service in 'Tidal', 'Spotify') {
    $card = FindId "${service}Card"
    Check ($null -ne $card -and $null -ne (Find "$service logo" 'Image')) "$service has its own card with its logo"
}
Check ((FindId 'TidalStatus').Current.Name -like 'Not connected*') "Tidal status before keys: $((FindId 'TidalStatus').Current.Name)"
$tidalCard = FindId 'TidalCard'
$tidalCard.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Start-Sleep 1
SetText (FindId 'TidalClientId') 'test-client-id'
(FindId 'TidalClientSecret').SetFocus()
[System.Windows.Forms.SendKeys]::SendWait('test-secret')
Invoke (FindId 'TidalSave')
for ($i = 0; $i -lt 30 -and (FindId 'TidalStatus').Current.Name -like 'Testing*'; $i++) { Start-Sleep 1 }
# Fake keys: Tidal refuses them (or, offline, can't be reached). Either way never "connected".
$status = (FindId 'TidalStatus').Current.Name
Check ($status -notlike 'Testing*' -and $status -notlike 'Searches include*' -and $status -notlike 'Not connected*') "fake keys are not shown as working: $status"
Check ($null -ne (FindId 'TidalRemove')) 'keys that failed the test can still be removed'
Invoke (FindId 'TidalRemove')
Check ((FindId 'TidalStatus').Current.Name -like 'Not connected*') "removed: $((FindId 'TidalStatus').Current.Name)"
Invoke (Find 'Undo' 'Button')
Check ((FindId 'TidalStatus').Current.Name -notlike 'Not connected*') "undo puts the keys back: $((FindId 'TidalStatus').Current.Name)"

$country = FindId 'CountryBox'
Check ($null -ne $country -and $country.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox') 'country is a dropdown'
$countryValue = $country.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection() | ForEach-Object { $_.Current.Name }
Check ($countryValue -eq 'Argentina') "country shows its name: $countryValue"

# Text recognition: what's installed, which one reads photos, and installing more.
Check ($null -ne (Find 'Text recognition (magazine photos)' 'Text')) 'settings section: Text recognition'
$ocrBox = FindId 'OcrLanguageBox'
$ocrValue = "$(if ($ocrBox) { $ocrBox.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection() | ForEach-Object { $_.Current.Name } })"
Check ($ocrValue -like 'Automatic*') "photos are read in: $ocrValue"
$installed = "$((FindId 'OcrInstalledList').Current.Name)"
Check ($installed.Length -gt 0) "installed text recognition languages are listed: $installed"
Check ($null -ne (FindId 'OcrInstallBox') -and $null -ne (FindId 'OcrInstallButton')) 'more languages can be installed'
Check ($null -ne (Find 'Windows language settings' 'Hyperlink')) 'a way out to Windows language settings'

$theme = FindId 'ThemeBox'
$theme.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Start-Sleep 1
Select-Item (Find 'Light' 'ListItem')
Check ($null -ne $theme) 'theme can be chosen'

Invoke (Find 'Reset layout' 'Button')
Check ((FindId 'PageTitle').Current.Name -eq 'To listen' -and $null -ne (Find 'Find an album' 'Edit')) 'Reset layout shows the original view'
$coverState = (Find 'Cover view*').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
Check ($coverState -eq 'Off') "reset layout goes back to the list view ($coverState)"
Stop-App

$uiFile = Get-Content (Join-Path $data 'ui.json') -Raw | ConvertFrom-Json
Check ($uiFile.Theme -eq 'light' -and $uiFile.TourSeen -eq $true) "theme saved, tour still seen (theme: $($uiFile.Theme))"
$settingsFile = Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
Check ($settingsFile.TidalClientId -eq 'test-client-id' -and $settingsFile.TidalClientSecret -eq 'test-secret') 'Tidal keys saved'

$crash = Join-Path $data 'crash.log'
Check (-not (Test-Path $crash)) 'no crash log (includes unobserved background task errors)'
if (Test-Path $crash) { Get-Content $crash | Select-Object -First 20 }

}
catch {
    Check $false "test stopped with an error: $_"
}
finally {
    Stop-TestApp
    Restore-Data
}

"`n$script:failures failure(s)"
exit $script:failures
