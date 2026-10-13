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
# Entry names with '/' as the zip format wants; Windows PowerShell's Compress-Archive and ZipFile write '\'.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = (Resolve-Path $out).Path
$stream = [IO.File]::Open((Join-Path (Resolve-Path dist).Path (Split-Path $zip -Leaf)), 'CreateNew')
$archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $root -Recurse -File) {
        $name = 'Queueify/' + $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, 'Optimal') | Out-Null
    }
}
finally {
    $archive.Dispose()
    $stream.Dispose()
}
"Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
