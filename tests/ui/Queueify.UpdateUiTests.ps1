# UI tests for updating Queueify from inside the app, driven through Windows UI Automation.
#
#   powershell -ExecutionPolicy Bypass -File tests\ui\Queueify.UpdateUiTests.ps1 -Exe <path to Queueify.exe>
#
# Never touches the exe under test or any real data: a copy of the exe runs from a scratch folder with a
# scratch LOCALAPPDATA, and a small web server on this PC plays GitHub (QUEUEIFY_UPDATE_SERVER), offering
# that same exe as version 99.0.0. Prints PASS/FAIL per check and exits with the number of failures.
param([Parameter(Mandatory)] [string]$Exe)
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

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("queueify-updatetest-" + [guid]::NewGuid().ToString('N'))
$folder = Join-Path $scratch 'Apps'
$copy = Join-Path $folder 'Queueify.exe'
$env:LOCALAPPDATA = Join-Path $scratch 'local'
$appCache = Join-Path $env:LOCALAPPDATA 'Queueify\app'
New-Item -ItemType Directory -Force $folder, (Join-Path $env:LOCALAPPDATA 'Queueify') | Out-Null
Copy-Item $Exe $copy
# English texts for the checks, and no quick tour in the way.
'{ "Language": "en", "TourSeen": true }' | Set-Content -Encoding UTF8 (Join-Path $env:LOCALAPPDATA 'Queueify\ui.json')

function Get-AppProcess {
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith("$appCache\", [StringComparison]::OrdinalIgnoreCase) }
}

# Waits for a Queueify window that wasn't there before.
function Wait-App([int[]]$before) {
    $p = $null
    for ($i = 0; $i -lt 60 -and -not $p; $i++) {
        Start-Sleep -Milliseconds 500
        $p = Get-AppProcess | Where-Object { $_.Id -notin $before -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    }
    Start-Sleep 3
    $script:proc = $p
    $script:root = if ($p) { $A::FromHandle($p.MainWindowHandle) } else { $null }
}

function Start-App { Start-Process $copy | Out-Null; Wait-App @() }

function Stop-App {
    Get-AppProcess | ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }
}

function All { $script:root.FindAll($Tree::Descendants, $Cond::TrueCondition) }
function Find([string]$name, [string]$type = '*') {
    All | Where-Object { $_.Current.Name -like $name -and $_.Current.ControlType.ProgrammaticName -like "ControlType.$type" } | Select-Object -First 1
}
function Select-Item($el) { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 800 }
function FindId([string]$id) { All | Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1 }
function Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 800 }
function Wait-Find([string]$name, [int]$seconds = 15) {
    for ($i = 0; $i -lt $seconds * 2; $i++) { $el = Find $name; if ($el) { return $el }; Start-Sleep -Milliseconds 500 }
    $null
}

# ── A pretend GitHub on this PC: path -> response, changed between the checks ──
$routes = [hashtable]::Synchronized(@{})
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$server = "http://127.0.0.1:$($listener.LocalEndpoint.Port)/"
$serve = [powershell]::Create()
$serve.AddScript({
    param($listener, $routes)
    while ($true) {
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::ASCII, $false, 1024, $true)
            $path = ($reader.ReadLine() -split ' ')[1]
            while ($reader.ReadLine()) { }
            $r = $routes[$path]
            if ($r -and $r.Delay) { Start-Sleep -Milliseconds $r.Delay }
            $status = if ($r -and $r.Status) { $r.Status } elseif ($r) { '200 OK' } else { '404 Not Found' }
            $body = if ($r) { $r.Body } else { [byte[]]@() }
            $type = if ($r) { $r.Type } else { 'text/plain' }
            $head = [System.Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $status`r`nContent-Type: $type`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n")
            $stream.Write($head, 0, $head.Length)
            $stream.Write($body, 0, $body.Length)
            $stream.Flush()
        }
        catch { }
        finally { $client.Close() }
    }
}).AddArgument($listener).AddArgument($routes) | Out-Null
$serving = $serve.BeginInvoke()
$env:QUEUEIFY_UPDATE_SERVER = $server

$newExe = [System.IO.File]::ReadAllBytes($Exe)
$sha256 = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
function Offer([string]$version, [string]$digest) {
    $name = "Queueify-$version-win-x64.exe"
    $json = @"
{ "tag_name": "v$version", "html_url": "${server}page", "draft": false, "prerelease": false,
  "assets": [ { "name": "$name", "digest": "sha256:$digest", "browser_download_url": "$server$name" } ] }
"@
    $routes['/latest'] = @{ Type = 'application/json'; Body = [System.Text.Encoding]::UTF8.GetBytes($json) }
    $routes["/$name"] = @{ Type = 'application/octet-stream'; Body = $newExe }
}

try {

# ── Found at the start: a dot on Help, and the offer itself in Help → About, nowhere else ──
function Open-Help { Select-Item (Find 'Help*' 'ListItem'); Start-Sleep 1 }
function Badge { Find 'Help, new version available' 'ListItem' }
'{ "Language": "en", "TourSeen": true, "PaneVisible": true, "WindowBounds": [50, 50, 1100, 700] }' | Set-Content -Encoding UTF8 (Join-Path $env:LOCALAPPDATA 'Queueify\ui.json')
Offer '99.0.0' ('0' * 64)
Start-App
Check ($null -ne $script:root) 'Queueify starts from the copy'
$badge = $null
for ($i = 0; $i -lt 30 -and -not $badge; $i++) { Start-Sleep -Milliseconds 500; $badge = Badge }
Check ($null -ne $badge) 'a newer release shows as a dot on Help'
Check ($null -eq (Find '*new version is out*')) 'nothing else about it on the To listen page'
Check ((Find 'Find an album' 'Edit').Current.HasKeyboardFocus) 'the keyboard stays in the search box'
Open-Help
Check ($null -ne (Wait-Find '*new version is out: Queueify 99.0.0*')) 'Help → About offers it'
Check ($null -eq (Badge) -and $null -ne (Find 'Help' 'ListItem')) 'opening Help clears the dot'

# ── A download that doesn't match the release is refused, the exe stays, and it can be tried again ──
$stamp = (Get-Item $copy).LastWriteTimeUtc
# From the keyboard: focus on the button, so it can follow to the bar's next one.
(FindId 'InstallUpdateButton').SetFocus()
Invoke (FindId 'InstallUpdateButton')
Check ($null -ne (Wait-Find "*doesn't match the release*")) 'a download that does not match is refused'
Check (((Get-Item $copy).LastWriteTimeUtc -eq $stamp) -and -not (Test-Path "$copy.download") -and -not (Test-Path "$copy.old")) 'the exe is left as it was'
Check ($null -ne (Find 'Release page')) 'the release page is offered too'
$retry = FindId 'InstallUpdateButton'
Check ($null -ne $retry -and $retry.Current.Name -eq 'Try again' -and $retry.Current.HasKeyboardFocus) 'Try again is offered, with the keyboard on it'

Stop-App

# ── A download that fails can be tried again; then the new exe takes the old one's place, and Queueify
#    restarts through it without a moment with no window ──
Offer '99.0.0' $sha256
# Slow to fail, so there's time to go back to adding albums meanwhile.
$routes['/Queueify-99.0.0-win-x64.exe'] = @{ Status = '404 Not Found'; Delay = 3000; Type = 'text/plain'; Body = [byte[]]@() }
Start-App
Open-Help
Invoke (FindId 'InstallUpdateButton')
(Find 'Find an album' 'Edit').SetFocus()
Check ($null -ne (Wait-Find '*GitHub answered with an error (404)*')) 'a failed download says so'
Check ((Find 'Find an album' 'Edit').Current.HasKeyboardFocus) 'a failure doesn''t take the keyboard from the search box'
Offer '99.0.0' $sha256
[System.IO.File]::SetLastWriteTimeUtc($copy, [datetime]'2001-01-01')
$before = @(Get-AppProcess | ForEach-Object Id)
Invoke (FindId 'InstallUpdateButton')
$gap = $false
for ($i = 0; $i -lt 300; $i++) {
    $windows = @(Get-AppProcess | Where-Object { $_.MainWindowHandle -ne 0 })
    if ($windows.Count -eq 0) { $gap = $true }
    if ($windows | Where-Object { $_.Id -notin $before }) { break }
    Start-Sleep -Milliseconds 100
}
Wait-App $before
Check ($null -ne $script:root) 'Queueify restarts after updating'
Check (-not $gap) 'a Queueify window is on screen the whole time'
Check ((Get-FileHash $copy -Algorithm SHA256).Hash.ToLowerInvariant() -eq $sha256 -and (Get-Item $copy).LastWriteTimeUtc.Year -gt 2001) 'the downloaded exe is in place, under the same name'
Check (-not (Test-Path "$copy.old") -and -not (Test-Path "$copy.download")) 'the old exe is removed after the restart'
Check (@(Get-AppProcess).Count -eq 1) 'the old copy closed'
Check ((Get-ChildItem $folder -Name) -join ',' -eq 'Queueify.exe') 'nothing else is left next to the exe'
Stop-App

# ── The latest version: About says so ──
$version = (Get-Item $Exe).VersionInfo.FileVersion -replace '^(\d+\.\d+\.\d+).*', '$1'
Offer $version $sha256
Start-App
Check ($null -eq (Badge)) 'no dot on Help when this is the latest version'
Open-Help
Invoke (FindId 'CheckUpdatesButton')
Check ($null -ne (Wait-Find 'You have the latest version.')) 'About: checking says this is the latest version'
Stop-App

# ── Turned off in Settings: no check at the start ──
Offer '99.0.0' $sha256
Start-App
Select-Item (Find 'Settings*' 'ListItem')
$toggle = FindId 'UpdateCheckToggle'
$toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep 1
Stop-App
Start-App
Start-Sleep 3
Check ($null -eq (Badge)) 'with checks turned off, nothing is offered at the start'
Open-Help
Invoke (FindId 'CheckUpdatesButton')
Check ($null -ne (Wait-Find '*new version is out: Queueify 99.0.0*')) 'checking by hand still finds it'
Stop-App
# Back on for the rest.
'{ "Language": "en", "TourSeen": true }' | Set-Content -Encoding UTF8 (Join-Path $env:LOCALAPPDATA 'Queueify\ui.json')

# ── Skip this version: not offered at the next start, still found by Check for updates ──
Offer '99.0.0' $sha256
Start-App
Open-Help
Invoke (FindId 'SkipUpdateButton')
Check ($null -eq (Find '*new version is out*')) 'Skip this version closes the offer'
Stop-App
Start-App
Start-Sleep 3
Check ($null -eq (Badge)) 'a skipped version is not offered at the next start'
Open-Help
Invoke (FindId 'CheckUpdatesButton')
Check ($null -ne (Wait-Find '*new version is out: Queueify 99.0.0*')) 'Check for updates still offers a skipped version'
Stop-App

# ── A new exe that matches its release but doesn't start: this window stays and says so ──
# (One byte changed in the marker at its very end: the starter finds no app appended and says it's damaged,
# whatever is already unpacked.)
'{ "Language": "en", "TourSeen": true }' | Set-Content -Encoding UTF8 (Join-Path $env:LOCALAPPDATA 'Queueify\ui.json')
$broken = [byte[]]$newExe.Clone()
$broken[$broken.Length - 1] = $broken[$broken.Length - 1] -bxor 0x40
$newExe = $broken
Offer '99.0.0' ([System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($broken)).Replace('-', '').ToLowerInvariant())
Start-App
Open-Help
$before = @(Get-AppProcess | ForEach-Object Id)
Invoke (FindId 'InstallUpdateButton')
# The starter's own message box: read it and press OK.
$box = $null
for ($i = 0; $i -lt 60 -and -not $box; $i++) {
    Start-Sleep -Milliseconds 500
    $box = $A::RootElement.FindAll($Tree::Children, $Cond::TrueCondition) | Where-Object { $_.Current.ClassName -eq '#32770' -and $_.Current.Name -eq 'Queueify' } | Select-Object -First 1
}
Check ($null -ne $box) 'the new exe says it is damaged'
if ($box) { Invoke ($box.FindAll($Tree::Descendants, $Cond::TrueCondition) | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' } | Select-Object -First 1) }
Check ($null -ne (Wait-Find "*is in place but didn't start, so your Queueify exe won't open*")) 'this window stays and says the new version did not start'
Check ($null -ne (Find 'Releases page' 'Button')) 'the releases page comes first'
Check ($null -ne (FindId 'InstallUpdateButton') -and (FindId 'InstallUpdateButton').Current.Name -eq 'Try again') 'and offers to download it again'
Check (@(Get-AppProcess | Where-Object { $_.Id -notin $before }).Count -eq 0) 'no second Queueify started'
}
finally {
    Stop-App
    $listener.Stop()
    $serve.Stop(); $serve.Dispose()
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

''
"$script:failures failure(s)"
exit $script:failures
