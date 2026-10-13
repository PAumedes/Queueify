<#
.SYNOPSIS
    Builds the portable app: dist\Queueify\Queueify.exe, a single trimmed file, and a copy named
    dist\Queueify-<version>-win-x64.exe for the release. Used by CI and the release workflow; works the
    same on a dev PC.
#>
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

# Fails the build when the download grows past this: it was 35 MB after leaving out the Windows App
# SDK's AI packages and trimming (ADR-0007), against 93 MB before.
$maxMB = 45

$version = ([regex]::Match((Get-Content -Raw Queueify\Queueify.csproj), '<Version>([^<]+)</Version>')).Groups[1].Value
$out = 'dist\Queueify'
if (Test-Path dist) { Remove-Item dist -Recurse -Force }

dotnet publish Queueify -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# One file and nothing else: no Data (a fresh download has no queue, keys or sync connection from the
# build machine) and nothing the exe needs beside it.
$extra = Get-ChildItem $out -Recurse | Where-Object Name -ne 'Queueify.exe'
if ($extra) { throw "The build should be Queueify.exe alone, but has: $($extra.Name -join ', ')" }

$exe = Get-Item "$out\Queueify.exe"
$mb = [math]::Round($exe.Length / 1MB, 1)
if ($mb -gt $maxMB) { throw "Queueify.exe is $mb MB, over the $maxMB MB budget. See ADR-0007." }

$download = "dist\Queueify-$version-win-x64.exe"
Copy-Item $exe.FullName $download

# Smoke test of the file users download, under its own name (a browser may also call it "Queueify (1).exe"):
# started from an empty folder and unpacked fresh, it must stay up without writing a crash log, and keep
# its data in local app data (a scratch one here, never the build machine's), not next to the exe.
$smoke = Join-Path ([IO.Path]::GetTempPath()) ("queueify-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory "$smoke\app", "$smoke\unpack", "$smoke\local" | Out-Null
$realLocal = $env:LOCALAPPDATA
try {
    Copy-Item $download "$smoke\app\Queueify (1).exe"
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = "$smoke\unpack"
    $env:LOCALAPPDATA = "$smoke\local"
    $p = Start-Process "$smoke\app\Queueify (1).exe" -PassThru
    # Up means a window: a crash before .NET starts writes no crash log and may linger in error reporting.
    for ($i = 0; $i -lt 30 -and -not $p.HasExited -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
    $alive = -not $p.HasExited -and $p.MainWindowHandle -ne 0
    if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit(10000) | Out-Null }
    $crash = "$smoke\local\Queueify\crash.log"
    if (-not $alive -or (Test-Path $crash)) {
        $why = if (Test-Path $crash) { Get-Content -Raw $crash } elseif ($p.HasExited) { "exit code $('0x{0:X8}' -f $p.ExitCode)" } else { 'no window after 15 s' }
        throw "The downloadable exe doesn't start: $why"
    }
    if ((Test-Path "$smoke\app\Data") -or -not (Test-Path "$smoke\local\Queueify\queueify.db")) {
        throw "The downloadable exe should keep its data in %LOCALAPPDATA%\Queueify, not next to itself."
    }
}
finally {
    $env:LOCALAPPDATA = $realLocal
    Remove-Item Env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue
    Remove-Item $smoke -Recurse -Force -ErrorAction SilentlyContinue
}
"Built $download ($mb MB); it starts under another name"
