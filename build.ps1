# Opacbar Zero-Setup Build Script
# Compiles Opacbar.exe using the built-in Windows .NET Framework compiler (csc.exe) or dotnet SDK if installed.

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$DistDir = Join-Path $RootDir "dist"
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir | Out-Null
}

$OutputExe = Join-Path $DistDir "Opacbar.exe"
$SourceFiles = @(
    (Join-Path $RootDir "src\AssemblyInfo.cs"),
    (Join-Path $RootDir "src\NativeMethods.cs"),
    (Join-Path $RootDir "src\AppConfig.cs"),
    (Join-Path $RootDir "src\TaskbarEngine.cs"),
    (Join-Path $RootDir "src\ThemeAndControls.cs"),
    (Join-Path $RootDir "src\SettingsForm.cs"),
    (Join-Path $RootDir "src\Program.cs")
)
$ManifestPath = Join-Path $RootDir "src\app.manifest"

# Locate built-in C# compiler on Windows
$CscCandidates = @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

$CscExe = $null
foreach ($candidate in $CscCandidates) {
    if (Test-Path $candidate) {
        $CscExe = $candidate
        break
    }
}

if ($null -ne $CscExe) {
    Write-Host "[Opacbar] Compiling standalone executable using $CscExe ..." -ForegroundColor White
    & $CscExe `
        /target:winexe `
        /optimize+ `
        /platform:anycpu `
        "/out:$OutputExe" `
        "/win32manifest:$ManifestPath" `
        /r:System.dll `
        /r:System.Core.dll `
        /r:System.Drawing.dll `
        /r:System.Windows.Forms.dll `
        /r:System.Web.Extensions.dll `
        $SourceFiles

    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed with exit code $LASTEXITCODE"
    }
}
elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Write-Host "[Opacbar] Compiling with dotnet SDK..." -ForegroundColor White
    dotnet build (Join-Path $RootDir "Opacbar.csproj") -c Release -o $DistDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
}
else {
    throw "Neither built-in .NET Framework csc.exe nor dotnet SDK was found."
}

$FileInfo = Get-Item $OutputExe
Write-Host "[Opacbar] Build succeeded: $($FileInfo.FullName) ($([Math]::Round($FileInfo.Length / 1KB, 1)) KB)" -ForegroundColor Green
