# Opacbar zero-setup build: compiles src\*.cs with the csc.exe that ships with Windows.
# No SDK, no NuGet. Fails loudly on any problem.

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SrcDir  = Join-Path $RootDir "src"
$DistDir = Join-Path $RootDir "dist"
$OutExe  = Join-Path $DistDir "Opacbar.exe"
$Manifest = Join-Path $SrcDir "app.manifest"
$Icon    = Join-Path $RootDir "assets\opacbar.ico"
$NativeBin = Join-Path $RootDir "native\bin"

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
if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir | Out-Null }
if (Test-Path $OutExe) {
    try { Remove-Item $OutExe -Force }
    catch { throw "Cannot replace $OutExe. Is Opacbar still running? Quit it from the tray first." }
}

$cscArgs = @(
    "/nologo",
    "/target:winexe",
    "/platform:anycpu",
    "/optimize+",
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

# ---- Native helper DLLs (optional) --------------------------------------------
if (Test-Path $NativeBin) {
    Get-ChildItem -Path $NativeBin -Filter "*.dll" -File | ForEach-Object {
        Copy-Item $_.FullName -Destination $DistDir -Force
        Write-Host "[Opacbar] copied $($_.Name)"
    }
}

$info = Get-Item $OutExe
Write-Host ("[Opacbar] Build succeeded: {0} ({1} KB)" -f $info.FullName, [Math]::Round($info.Length / 1KB, 1)) -ForegroundColor Green
