# SPDX-License-Identifier: MIT
# Copyright (c) 2026 moller.studio
# OpacityBar: https://github.com/mollerstudio/OpacityBar

# OpacityBar build: compiles the app and the installer with the csc.exe that ships with Windows.
# No SDK, no NuGet, no Visual Studio. Fails loudly on any problem.
#
#   .\build.ps1                  dist\OpacityBar.exe, and dist\OpacityBarSetup.exe when both helper DLLs
#                                (dist\OpacityBarTAP.x64.dll, dist\OpacityBarTAP.arm64.dll) are present
#   .\build.ps1 -OutExe <path>   build only the app, somewhere else (e.g. while dist\OpacityBar.exe runs)
#   .\build.ps1 -NoInstaller     skip the installer
#
# Branding files in assets\ (generated from the logo PDF by tools\make-assets.ps1):
#   assets\OpacityBar.ico   app + installer icon (black logo)
#   assets\tray-black.ico   tray icon on light taskbars, and the fallback everywhere
#   assets\tray-white.ico   tray icon on dark taskbars
#   assets\logo-black.png   installer logo, light theme (and fallback)
#   assets\logo-white.png   installer logo, dark theme

param(
    [string]$OutExe = "",
    [switch]$NoInstaller
)

$ErrorActionPreference = "Stop"

$Root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$SrcDir    = Join-Path $Root "src"
$DistDir   = Join-Path $Root "dist"
$Assets    = Join-Path $Root "assets"
$Manifest  = Join-Path $SrcDir "app.manifest"
$NativeBin = Join-Path $Root "native\bin"
$toDist    = [string]::IsNullOrEmpty($OutExe)
if ($toDist) { $OutExe = Join-Path $DistDir "OpacityBar.exe" }

function Say($msg) { Write-Host "[OpacityBar] $msg" }

# ---- Compiler ----------------------------------------------------------------
$fwRoot  = Join-Path $env:WINDIR "Microsoft.NET"
$isArm64 = ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") -or ($env:PROCESSOR_ARCHITEW6432 -eq "ARM64")
$candidates = @()
if ($isArm64) { $candidates += (Join-Path $fwRoot "FrameworkArm64\v4.0.30319\csc.exe") }
$candidates += (Join-Path $fwRoot "Framework64\v4.0.30319\csc.exe")
$candidates += (Join-Path $fwRoot "Framework\v4.0.30319\csc.exe")
$Csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Csc) { throw "csc.exe not found. Looked in: $($candidates -join ', ')" }
Say "csc: $Csc"

$CommonArgs = @(
    "/nologo", "/target:winexe", "/platform:anycpu", "/optimize+", "/codepage:65001",
    "/nowarn:420",   # volatile field passed by ref to Interlocked: intended
    "/win32manifest:$Manifest",
    "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll"
)
$AppIcon = Join-Path $Assets "OpacityBar.ico"
if (Test-Path $AppIcon) { $CommonArgs += "/win32icon:$AppIcon" }

function Compile([string]$out, [string[]]$extra, [string[]]$sources) {
    $dir = Split-Path -Parent $out
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    if (Test-Path $out) {
        try { Remove-Item $out -Force }
        catch { throw "Cannot replace $out. Is it still running? Quit it first, or build elsewhere with -OutExe <path>." }
    }
    & $Csc @CommonArgs "/out:$out" @extra @sources
    if ($LASTEXITCODE -ne 0) { throw "Compiling $(Split-Path -Leaf $out) failed (csc exit code $LASTEXITCODE)" }
    $kb = [Math]::Round((Get-Item $out).Length / 1KB, 1)
    Write-Host "[OpacityBar] built $out ($kb KB)" -ForegroundColor Green
}

# ---- App -----------------------------------------------------------------------
$AppSources = @(Get-ChildItem -Path $SrcDir -Filter "*.cs" -File | Sort-Object Name | ForEach-Object { $_.FullName })
$AppExtra = @("/r:System.Web.Extensions.dll")
foreach ($n in "tray-black.ico", "tray-white.ico") {
    $f = Join-Path $Assets $n
    if (Test-Path $f) { $AppExtra += "/resource:$f,$n"; Say "embedding assets\$n" }
}
# UI font: Outfit (SIL Open Font License, assets\fonts\OFL.txt), embedded in the app and the installer.
$FontRes = @()
foreach ($n in "Outfit-Regular.ttf", "Outfit-SemiBold.ttf") {
    $f = Join-Path $Assets "fonts\$n"
    if (Test-Path $f) { $FontRes += "/resource:$f,$n"; Say "embedding assets\fonts\$n" }
}
$AppExtra += $FontRes
Compile $OutExe $AppExtra $AppSources
if (-not $toDist) { return }

# Helper DLLs built locally (optional; CI puts them straight into dist\).
if (Test-Path $NativeBin) {
    Get-ChildItem -Path $NativeBin -Filter "OpacityBarTAP.*.dll" -File | ForEach-Object {
        Copy-Item $_.FullName -Destination $DistDir -Force
        Say "copied $($_.Name)"
    }
}

# ---- Installer -----------------------------------------------------------------
if ($NoInstaller) { return }
$helpers = @("OpacityBarTAP.x64.dll", "OpacityBarTAP.arm64.dll")
$missing = $helpers | Where-Object { -not (Test-Path (Join-Path $DistDir $_)) }
if ($missing) {
    Say "skipping the installer: missing in dist\: $($missing -join ', ') (GitHub Actions builds them)"
    return
}

$SetupExtra = @(
    "/resource:$OutExe,payload/OpacityBar.exe",
    "/resource:$(Join-Path $DistDir 'OpacityBarTAP.x64.dll'),payload/OpacityBarTAP.x64.dll",
    "/resource:$(Join-Path $DistDir 'OpacityBarTAP.arm64.dll'),payload/OpacityBarTAP.arm64.dll",
    "/main:OpacityBar.Setup.SetupProgram"
)
foreach ($n in "logo-black.png", "logo-white.png") {
    $f = Join-Path $Assets $n
    if (Test-Path $f) { $SetupExtra += "/resource:$f,$n"; Say "embedding assets\$n in the installer" }
}
$SetupExtra += $FontRes
# Taskbar-button icon of the installer window: white logo on a dark taskbar, black on a light one.
foreach ($n in "tray-black.ico", "tray-white.ico") {
    $f = Join-Path $Assets $n
    if (Test-Path $f) { $SetupExtra += "/resource:$f,$n" }
}

# The installer shares the app's theme, controls and install plumbing.
$SetupSources = @("AppInfo.cs", "Lifecycle.cs", "NativeMethods.cs", "ThemeAndControls.cs") | ForEach-Object { Join-Path $SrcDir $_ }
$SetupSources += Get-ChildItem -Path (Join-Path $Root "installer") -Filter "*.cs" -File | ForEach-Object { $_.FullName }
Compile (Join-Path $DistDir "OpacityBarSetup.exe") $SetupExtra $SetupSources
