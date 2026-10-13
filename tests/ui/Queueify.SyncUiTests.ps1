# UI test for connecting Sync, against the local Firestore emulator.
#
#   1. Start the emulator with tools\firestore.rules on 127.0.0.1:8080 (see README, "Running the tests").
#   2. powershell -ExecutionPolicy Bypass -File tests\ui\Queueify.SyncUiTests.ps1 -Exe <path to Queueify.exe>
#
# Data files next to the exe are backed up and restored, like in Queueify.UiTests.ps1.
param([Parameter(Mandatory)] [string]$Exe, [string]$Emulator = '127.0.0.1:8080')
$Exe = (Resolve-Path $Exe).Path

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]
$Cond = [System.Windows.Automation.Condition]
$script:failures = 0

function Check([bool]$ok, [string]$what) {
    if (-not $ok) { $script:failures++ }
    '{0}  {1}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what
}
function All { $script:root.FindAll($Tree::Descendants, $Cond::TrueCondition) }
function Find([string]$name, [string]$type = '*') {
    All | Where-Object { $_.Current.Name -like $name -and $_.Current.ControlType.ProgrammaticName -like "ControlType.$type" } | Select-Object -First 1
}
function FindId([string]$id) { All | Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1 }
function Invoke($el) {
    $pattern = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
    else { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    Start-Sleep -Milliseconds 1200
}
function SetText($el, [string]$text) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }

$data = Join-Path (Split-Path $Exe) 'Data'
$files = 'queueify.db', 'settings.json', 'ui.json', 'crash.log'
$backup = Join-Path ([System.IO.Path]::GetTempPath()) ("queueify-uitest-backup-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $backup | Out-Null
# Only the copy under test is stopped; a Queueify running from another folder is left alone.
function Stop-TestApp {
    Get-Process ([IO.Path]::GetFileNameWithoutExtension($Exe)) -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } |
        ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }
}
Stop-TestApp

# Whatever happens below (a failed step, no emulator, Ctrl+C), the app is closed and the real data put back.
# The backup itself happens inside, so even a half-done backup is restored.
try {

foreach ($f in $files) { $p = Join-Path $data $f; if (Test-Path $p) { Move-Item $p (Join-Path $backup $f) -ErrorAction Stop } }

# Skip the first-run tour so it doesn't cover the dialog.
New-Item -ItemType Directory -Force $data | Out-Null
'{ "TourSeen": true, "Language": "en" }' | Set-Content -Encoding UTF8 (Join-Path $data 'ui.json')

$env:QUEUEIFY_FIRESTORE_EMULATOR = $Emulator
$proc = Start-Process $Exe -PassThru
for ($i = 0; $i -lt 60 -and $proc.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $proc.Refresh() }
Start-Sleep 3
$script:root = $A::FromHandle($proc.MainWindowHandle)

$sync = Find 'Sync*' 'ListItem'
Check ($sync.Current.Name -like 'Sync: keep*') "sync item before connecting: $($sync.Current.Name)"
# Triggered twice in a row (like a quick double-click): must open one dialog, not crash.
$sync.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$sync.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep 2
Check ($null -ne (Find 'Copy security rules' 'Button')) 'setup dialog lists the Firebase steps (opened by a double trigger)'
Check (-not (Test-Path (Join-Path $data 'crash.log'))) 'a double trigger does not crash'
Check ((FindId 'PageTitle').Current.Name -eq 'To listen') 'the current page stays "To listen"'

Invoke (Find 'Connect' 'Button')
Check ($null -ne (Find 'Paste the Firebase project ID*')) 'an empty project id is rejected inside the dialog'

SetText (Find 'Firebase project ID' 'Edit') 'https://console.firebase.google.com/u/0/project/demo-queueify/firestore'
Invoke (Find 'Connect' 'Button')
Start-Sleep 3
$code = Find 'Sync code' 'Edit'
$codeText = if ($code) { $code.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } else { '' }
Check ($codeText -like 'queueify:demo-queueify:*') "connected; sync code shown: $codeText"
Invoke (FindId 'CloseButton')
Check ($null -ne (Find 'Synced*' 'ListItem')) "sync item after connecting: $((Find '*ynced*' 'ListItem').Current.Name)"

SetText (Find 'Find an album' 'Edit') 'Virus - Locura 1985'
Start-Sleep 2
Invoke (Find 'Add *without a link' 'Hyperlink')
Start-Sleep 4

$proc.CloseMainWindow() | Out-Null
if (-not $proc.WaitForExit(10000)) { $proc.Kill() }
$settings = Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
Check ($settings.CloudProjectId -eq 'demo-queueify' -and $settings.CloudQueueKey.Length -ge 32) 'connection saved'

$key = $settings.CloudQueueKey
$query = '{"structuredQuery":{"from":[{"collectionId":"albums"}]}}'
$res = Invoke-RestMethod -Method Post -ContentType 'application/json' -Body $query "http://$Emulator/v1/projects/demo-queueify/databases/(default)/documents/queues/${key}:runQuery"
$albums = @($res | Where-Object { $_.document } | ForEach-Object { "$($_.document.fields.artist.stringValue) / $($_.document.fields.album.stringValue) / $($_.document.fields.year.integerValue)" })
Check ($albums -contains 'Virus / Locura / 1985') "album reached Firestore: $($albums -join '; ')"
Check (-not (Test-Path (Join-Path $data 'crash.log'))) 'no crash log'

}
catch {
    Check $false "test stopped with an error: $_"
}
finally {
    Stop-TestApp
    # The backup folder is only deleted when every original made it back.
    $allBack = $true
    foreach ($f in $files) {
        $p = Join-Path $data $f
        $saved = Join-Path $backup $f
        try {
            if (Test-Path $saved) { Move-Item $saved $p -Force -ErrorAction Stop }
            elseif (Test-Path $p) { Remove-Item $p -ErrorAction Stop }
        }
        catch {
            $allBack = $false
            "WARNING: couldn't restore $f ($_). The original is still in $backup"
        }
    }
    if ($allBack) { Remove-Item $backup -Recurse -ErrorAction SilentlyContinue }
}
"`n$script:failures failure(s)"
exit $script:failures
