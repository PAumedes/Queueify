<#
.SYNOPSIS
    Runs the Cloud Firestore emulator with Queueify's security rules on 127.0.0.1:8080, for the sync
    tests. Needs Java 21 or newer. The emulator jar (from firebase-tools) is downloaded once into .cache\.

.EXAMPLE
    tools\firestore-emulator.ps1               # runs until Ctrl+C
    tools\firestore-emulator.ps1 -Background   # starts it, waits until it answers, returns
    $env:QUEUEIFY_FIRESTORE_EMULATOR = '127.0.0.1:8080'; dotnet test --project Queueify.Tests
#>
param([switch]$Background, [int]$Port = 8080)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$jar = '.cache\cloud-firestore-emulator-v1.22.0.jar'
if (-not (Test-Path $jar)) {
    New-Item -ItemType Directory -Force .cache | Out-Null
    Invoke-WebRequest -UseBasicParsing 'https://storage.googleapis.com/firebase-preview-drop/emulator/cloud-firestore-emulator-v1.22.0.jar' -OutFile $jar
}

$arguments = '-jar', $jar, '--host', '127.0.0.1', '--port', $Port, '--rules', 'tools\firestore.rules', '--project_id', 'demo-queueify'
if (-not $Background) {
    & java @arguments
    return
}

$process = Start-Process java -ArgumentList $arguments -PassThru -WindowStyle Hidden
for ($i = 0; $i -lt 60; $i++) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', $Port)
        "Firestore emulator ready on 127.0.0.1:$Port (process $($process.Id))"
        return
    } catch [System.Net.Sockets.SocketException] {
        if ($process.HasExited) { throw "The emulator stopped (exit code $($process.ExitCode))." }
        Start-Sleep 1
    } finally {
        $client.Dispose()
    }
}
throw 'The Firestore emulator did not start within a minute.'