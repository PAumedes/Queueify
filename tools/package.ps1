<#
.SYNOPSIS
    Builds the portable app: dist\Queueify\Queueify.exe, one file (the starter with the app appended,
    ADR-0008), and a copy named dist\Queueify-<version>-win-x64.exe for the release. Used by CI and the
    release workflow; works the same on a dev PC with Visual Studio's C++ build tools.
#>
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

# Fails the build when the download grows past this. 1.1.0 was a 93 MB zip; 1.2.0 a 35 MB exe (ADR-0007);
# compiled ahead of time and packed with Brotli it is about 19 MB (ADR-0008).
$maxMB = 24

# Native AOT links with the C++ build tools and finds them through vswhere, which isn't on PATH by default.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path "$installer\vswhere.exe") -and -not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) { $env:PATH = "$installer;$env:PATH" }

$version = ([regex]::Match((Get-Content -Raw Queueify\Queueify.csproj), '<Version>([^<]+)</Version>')).Groups[1].Value
$out = 'dist\Queueify'
if (Test-Path dist) { Remove-Item dist -Recurse -Force }

dotnet publish Queueify -c Release -o dist\build\app
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish Queueify failed' }
dotnet publish Queueify.Launcher -c Release -o dist\build\launcher
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish Queueify.Launcher failed' }
# A fresh download has no queue, keys or sync connection from the build machine.
if (Test-Path dist\build\app\Data) { Remove-Item dist\build\app\Data -Recurse -Force }

# The starter must load its imports from System32 only (LOAD_LIBRARY_SEARCH_SYSTEM32), not from the folder
# the download sits in: checked with the linker's own dumpbin, which comes with the C++ build tools.
$vswhere = Get-Command vswhere.exe -ErrorAction SilentlyContinue
$vc = if ($vswhere) { & $vswhere.Source -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe' | Select-Object -First 1 }
if (-not $vc) { throw 'dumpbin.exe not found: install the C++ build tools (see CONTRIBUTING.md)' }
if (-not (& $vc /loadconfig dist\build\launcher\Queueify.Launcher.exe | Select-String '^\s*0800 Dependent Load Flag')) {
    throw 'The starter would load dlls from the folder it sits in (DEPENDENTLOADFLAG missing).'
}

New-Item -ItemType Directory $out | Out-Null
dotnet run --project tools\Pack -c Release -- dist\build\launcher\Queueify.Launcher.exe dist\build\app "$out\Queueify.exe"
if ($LASTEXITCODE -ne 0) { throw 'packing the app into the starter failed' }

$exe = Get-Item "$out\Queueify.exe"
$mb = [math]::Round($exe.Length / 1MB, 1)
if ($mb -gt $maxMB) { throw "Queueify.exe is $mb MB, over the $maxMB MB budget. See ADR-0008." }

$download = "dist\Queueify-$version-win-x64.exe"
Copy-Item $exe.FullName $download

# Smoke test of the file users download, under its own name (a browser may also call it "Queueify (1).exe"),
# from an empty folder with a scratch local app data (never the build machine's): the starter unpacks the
# app there and exits, and the app must show its window without a crash log, with its data in local app
# data and nothing next to the exe.
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
namespace QueueifySmoke {
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid FormatId; public uint Pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    public static class Window {
        [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        public static string RelaunchCommand(IntPtr hwnd) {
            var iid = typeof(IPropertyStore).GUID; IPropertyStore store;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) < 0) return null;
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Pid = 2 };
            PropVariant value; store.GetValue(ref key, out value);
            return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
    }
}
'@
$smoke = Join-Path ([IO.Path]::GetTempPath()) ("queueify-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory "$smoke\app", "$smoke\local" | Out-Null
$realLocal = $env:LOCALAPPDATA
$app = $null
try {
    Copy-Item $download "$smoke\app\Queueify (1).exe"
    $env:LOCALAPPDATA = "$smoke\local"
    $starter = Start-Process "$smoke\app\Queueify (1).exe" -PassThru
    $cache = "$smoke\local\Queueify\app\"
    # Up means a window: a crash before .NET starts writes no crash log and may linger in error reporting.
    for ($i = 0; $i -lt 60 -and -not $app; $i++) {
        Start-Sleep -Milliseconds 500
        $app = Get-Process Queueify -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($cache, [StringComparison]::OrdinalIgnoreCase) -and $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
    }
    $starterExit = if ($starter.WaitForExit(10000)) { $starter.ExitCode } else { 'still running' }
    $crash = "$smoke\local\Queueify\crash.log"
    if (-not $app -or (Test-Path $crash) -or $starterExit -ne 0) {
        $why = if (Test-Path $crash) { Get-Content -Raw $crash } elseif ($starterExit -ne 0) { "the starter ended with $starterExit" } else { 'no window after 30 s' }
        throw "The downloadable exe doesn't start: $why"
    }
    if ((Test-Path "$smoke\app\Data") -or -not (Test-Path "$smoke\local\Queueify\queueify.db")) {
        throw "The downloadable exe should keep its data in %LOCALAPPDATA%\Queueify, not next to itself."
    }
    # A taskbar pin must start the downloaded exe, not the unpacked copy the next version removes.
    $relaunch = [QueueifySmoke.Window]::RelaunchCommand($app.MainWindowHandle)
    if ($relaunch -ne "`"$smoke\app\Queueify (1).exe`"") { throw "A taskbar pin would start '$relaunch' instead of the downloaded exe." }
}
finally {
    if ($app) { $app.Kill(); $app.WaitForExit(10000) | Out-Null }
    $env:LOCALAPPDATA = $realLocal
    Start-Sleep 1
    Remove-Item $smoke -Recurse -Force -ErrorAction SilentlyContinue
}
"Built $download ($mb MB); it starts under another name"
