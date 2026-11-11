# Opacbar zero-setup build: compiles src\*.cs with the csc.exe that ships with Windows.
# No SDK, no NuGet. Fails loudly on any problem.
#
#   .\build.ps1                     builds dist\Opacbar.exe (+ copies native\bin\*.dll if present)
#                                   and dist\OpacbarSetup.exe when Inno Setup 6 is installed
#   .\build.ps1 -OutExe <path>      builds the exe somewhere else (e.g. while dist\Opacbar.exe is running)
#   .\build.ps1 -NoInstaller        skips the installer

param(
    [string]$OutExe = "",
    [switch]$NoInstaller
)

$ErrorActionPreference = "Stop"

$RootDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$SrcDir    = Join-Path $RootDir "src"
$DistDir   = Join-Path $RootDir "dist"
$Manifest  = Join-Path $SrcDir "app.manifest"
$Icon      = Join-Path $RootDir "assets\opacbar.ico"
$NativeBin = Join-Path $RootDir "native\bin"
$Iss       = Join-Path $RootDir "installer\Opacbar.iss"
$toDist    = [string]::IsNullOrEmpty($OutExe)
if ($toDist) { $OutExe = Join-Path $DistDir "Opacbar.exe" }

# ---- Sources -----------------------------------------------------------------
$Sources = @(Get-ChildItem -Path $SrcDir -Filter "*.cs" -File | Sort-Object Name | ForEach-Object { $_.FullName })
if ($Sources.Count -eq 0) { throw "No source files found in $SrcDir" }
if (-not (Test-Path $Manifest)) { throw "Missing manifest: $Manifest" }

# ---- Compiler ----------------------------------------------------------------
$fwRoot = Join-Path $env:WINDIR "Microsoft.NET"
$isArm64 = ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") -or ($env:PROCESSOR_ARCHITEW6432 -eq "ARM64")

$candidates = @()
if ($isArm64) { $candidates += (Join-Path $fwRoot "FrameworkArm64\v4.0.30319\csc.exe") }
$candidates += (Join-Path $fwRoot "Framework64\v4.0.30319\csc.exe")
$candidates += (Join-Path $fwRoot "Framework\v4.0.30319\csc.exe")

$Csc = $null
foreach ($c in $candidates) {
    if (Test-Path $c) { $Csc = $c; break }
}
if (-not $Csc) { throw "csc.exe not found. Looked in: $($candidates -join ', ')" }

# ---- Output ------------------------------------------------------------------
$outDir = Split-Path -Parent $OutExe
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
if (Test-Path $OutExe) {
    try { Remove-Item $OutExe -Force }
    catch { throw "Cannot replace $OutExe. Is Opacbar still running? Quit it from the tray first, or build elsewhere with -OutExe <path>." }
}

$cscArgs = @(
    "/nologo",
    "/target:winexe",
    "/platform:anycpu",
    "/optimize+",
    "/codepage:65001",
    "/out:$OutExe",
    "/win32manifest:$Manifest",
    "/r:System.dll",
    "/r:System.Core.dll",
    "/r:System.Drawing.dll",
    "/r:System.Windows.Forms.dll",
    "/r:System.Web.Extensions.dll"
)
if (Test-Path $Icon) { $cscArgs += "/win32icon:$Icon" }
$cscArgs += $Sources

Write-Host "[Opacbar] csc: $Csc"
Write-Host "[Opacbar] compiling $($Sources.Count) files..."
& $Csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed (csc exit code $LASTEXITCODE)" }
if (-not (Test-Path $OutExe)) { throw "csc reported success but $OutExe was not created" }

$info = Get-Item $OutExe
Write-Host ("[Opacbar] Build succeeded: {0} ({1} KB)" -f $info.FullName, [Math]::Round($info.Length / 1KB, 1)) -ForegroundColor Green
if (-not $toDist) { return }

# ---- Native helper DLLs (optional) --------------------------------------------
if (Test-Path $NativeBin) {
    Get-ChildItem -Path $NativeBin -Filter "*.dll" -File | ForEach-Object {
        Copy-Item $_.FullName -Destination $DistDir -Force
        Write-Host "[Opacbar] copied $($_.Name)"
    }
}

# ---- Installer (optional: needs Inno Setup 6 and both helper DLLs) ---------------
if ($NoInstaller) { return }
$Iscc = $null
foreach ($c in @(
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"))) {
    if ($c -and (Test-Path $c)) { $Iscc = $c; break }
}
if (-not $Iscc) {
    Write-Host "[Opacbar] Inno Setup 6 (ISCC.exe) not found: skipping the installer. Get it from https://jrsoftware.org/isinfo.php"
    return
}
$missing = @("OpacbarTAP.x64.dll", "OpacbarTAP.arm64.dll") | Where-Object { -not (Test-Path (Join-Path $DistDir $_)) }
if ($missing) {
    Write-Host "[Opacbar] skipping the installer: missing in dist\: $($missing -join ', ') (build the helper first)"
    return
}
Write-Host "[Opacbar] iscc: $Iscc"
& $Iscc /Q $Iss
if ($LASTEXITCODE -ne 0) { throw "Installer build failed (ISCC exit code $LASTEXITCODE)" }
Write-Host ("[Opacbar] Installer: {0}" -f (Join-Path $DistDir "OpacbarSetup.exe")) -ForegroundColor Green
