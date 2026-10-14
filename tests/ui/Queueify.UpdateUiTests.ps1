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
            $status = if ($r) { '200 OK' } else { '404 Not Found' }
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

# ── A download that doesn't match the release is refused, and the exe stays ──
Offer '99.0.0' ('0' * 64)
Start-App
Check ($null -ne $script:root) 'Queueify starts from the copy'
Check ($null -ne (Wait-Find '*Queueify 99.0.0 is available*')) 'a newer release is offered at the start'
$stamp = (Get-Item $copy).LastWriteTimeUtc
Invoke (FindId 'InstallUpdateButton')
Check ($null -ne (Wait-Find "*doesn't match the release*")) 'a download that does not match is refused'
Check (((Get-Item $copy).LastWriteTimeUtc -eq $stamp) -and -not (Test-Path "$copy.download") -and -not (Test-Path "$copy.old")) 'the exe is left as it was'
Check ($null -ne (Find 'Release page')) 'the release page is offered instead'
Stop-App

# ── The latest version: About says so ──
$version = (Get-Item $Exe).VersionInfo.FileVersion -replace '^(\d+\.\d+\.\d+).*', '$1'
Offer $version $sha256
Start-App
Check ($null -eq (Find '*is available*')) 'no offer when this is the latest version'
Select-Item (Find 'Help*' 'ListItem')
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
Check ($null -eq (Find '*is available*')) 'with checks turned off, nothing is offered at the start'
Select-Item (Find 'Help*' 'ListItem')
Invoke (FindId 'CheckUpdatesButton')
Check ($null -ne (Wait-Find '*Queueify 99.0.0 is available*')) 'checking by hand still finds it'

# ── Updating: the new exe takes the old one's place and Queueify restarts through it ──
[System.IO.File]::SetLastWriteTimeUtc($copy, [datetime]'2001-01-01')
$before = @(Get-AppProcess | ForEach-Object Id)
Invoke (FindId 'InstallUpdateButton')
Wait-App $before
Check ($null -ne $script:root) 'Queueify restarts after updating'
Check ((Get-FileHash $copy -Algorithm SHA256).Hash.ToLowerInvariant() -eq $sha256 -and (Get-Item $copy).LastWriteTimeUtc.Year -gt 2001) 'the downloaded exe is in place, under the same name'
Check (-not (Test-Path "$copy.old") -and -not (Test-Path "$copy.download")) 'the old exe is removed after the restart'
Check (@(Get-AppProcess).Count -eq 1) 'the old copy closed'
Check ((Get-ChildItem $folder -Name) -join ',' -eq 'Queueify.exe') 'nothing else is left next to the exe'

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
