<#
.SYNOPSIS
    Builds the portable app into dist\Queueify and zips it as dist\Queueify-<version>-win-x64.zip.
    Used by CI and the release workflow; works the same on a dev PC.
#>
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$version = ([regex]::Match((Get-Content -Raw Queueify\Queueify.csproj), '<Version>([^<]+)</Version>')).Groups[1].Value
$out = 'dist\Queueify'
if (Test-Path dist) { Remove-Item dist -Recurse -Force }

dotnet publish Queueify -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# A fresh download must start empty: no queue, keys or sync connection from the build machine.
if (Test-Path "$out\Data") { Remove-Item "$out\Data" -Recurse -Force }
foreach ($required in 'Queueify.exe', 'Queueify.pri', 'MainWindow.xbf') {
    if (-not (Test-Path "$out\$required")) { throw "The build is missing $required" }
}

$zip = "dist\Queueify-$version-win-x64.zip"
Compress-Archive -Path $out -DestinationPath $zip
"Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
