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
"Built $download ($mb MB)"
