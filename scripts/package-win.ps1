# SPDX-License-Identifier: AGPL-3.0-only
#
# Package Knox Studio for Windows: build the native engine, publish the self-contained
# app, then build an Inno Setup installer (scripts/knox.iss).
#
# Usage: pwsh scripts/package-win.ps1 [x64|arm64]   (default: x64)
# Output: dist/Knox-Studio-Setup-<version>-<arch>.exe

param(
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64'
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$Version   = (Get-Content VERSION -Raw).Trim()
$Rid       = "win-$Arch"
$CmakeArch = if ($Arch -eq 'arm64') { 'ARM64' } else { 'x64' }
$Native    = "src/native/knox.engine/build-$Arch"
$PubDir    = Join-Path (Get-Location) "dist/publish-$Arch"

Write-Host "==> Building native engine ($Arch / WASAPI)…"
# Let CMake pick the default (newest installed) Visual Studio generator instead of
# hardcoding one — "Visual Studio 17 2022" fails on images that ship only VS 2026.
# -A selects the target architecture; it is valid for any VS generator.
cmake -A $CmakeArch -S src/native/knox.engine -B $Native
cmake --build $Native --config Release

# Locate the freshly built engine DLL. The VS (multi-config) generator usually writes
# to build-<arch>/Release, but the exact layout varies across CMake/VS versions and CI
# images — so resolve it from the actual output instead of hardcoding the path.
$dll = Get-ChildItem -Path $Native -Recurse -Filter knox_engine.dll -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dll) { throw "knox_engine.dll not found under $Native after the native build." }
$NativeOut = $dll.Directory.FullName
Write-Host "    native artifacts: $NativeOut"

Write-Host "==> Publishing managed app ($Rid, self-contained)…"
if (Test-Path $PubDir) { Remove-Item -Recurse -Force $PubDir }
dotnet publish src/managed/Knox.App -c Release -r $Rid --self-contained true `
  "-p:KnoxNativeDir=$NativeOut" -o $PubDir

# The csproj copies the native artifacts into the publish output for a win RID;
# copy them explicitly too as a safety net.
Copy-Item (Join-Path $NativeOut 'knox_engine.dll') $PubDir -Force
$scanWorker = Join-Path $NativeOut 'knox-scanworker.exe'
if (Test-Path $scanWorker) { Copy-Item $scanWorker $PubDir -Force }

Write-Host "==> Building installer with Inno Setup…"
function Find-ISCC {
    # PATH first.
    $cmd = (Get-Command iscc -ErrorAction SilentlyContinue)?.Source
    if ($cmd) { return $cmd }
    # Both Program Files roots (Inno Setup 6.4+ installs as 64-bit into %ProgramFiles%).
    $candidates = @(
        (Join-Path ${env:ProgramFiles}       'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)}  'Inno Setup 6\ISCC.exe')
    )
    # Registry install location (per-machine 64/32-bit + per-user).
    $regKeys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )
    foreach ($k in $regKeys) {
        $loc = (Get-ItemProperty -Path $k -ErrorAction SilentlyContinue).InstallLocation
        if ($loc) { $candidates += (Join-Path $loc 'ISCC.exe') }
    }
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}
$iscc = Find-ISCC
if (-not $iscc) { throw "Inno Setup (ISCC.exe) not found — install Inno Setup 6.3+ (winget install JRSoftware.InnoSetup)." }
Write-Host "    using $iscc"
& $iscc "/DAppVersion=$Version" "/DArch=$Arch" "/DPubDir=$PubDir" (Join-Path $PSScriptRoot 'knox.iss')

Write-Host "==> Done: dist/Knox-Studio-Setup-v$Version-$Arch.exe"
