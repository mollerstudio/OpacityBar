# SPDX-License-Identifier: MIT
# Copyright (c) 2026 moller.studio
# OpacityBar: https://github.com/mollerstudio/OpacityBar

# Generates every branding file in assets\ (and docs\images\logo.png) from the logo PDF.
#
#   .\tools\make-assets.ps1 -Pdf "path\to\OpacityBar Logo.pdf"
#
# The PDF has two square pages: page 1 = the black logo (used on light themes, and the default
# everywhere a single icon is needed), page 2 = the white logo (used on dark themes).
# Uses only what ships with Windows: the Windows.Data.Pdf renderer and System.Drawing.

param(
    [Parameter(Mandatory = $true)][string]$Pdf,
    [int]$BlackPage = 1,
    [int]$WhitePage = 2
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Assets = Join-Path $Root "assets"
$Docs = Join-Path $Root "docs\images"
New-Item -ItemType Directory -Force $Assets, $Docs | Out-Null
Add-Type -AssemblyName System.Drawing, System.Runtime.WindowsRuntime

# ---- Render the PDF pages ------------------------------------------------------------
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$ext = [System.WindowsRuntimeSystemExtensions].GetMethods()
$asTaskOp = ($ext | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
$asTaskAct = ($ext | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' })[0]
function Await($op, [Type]$t) { $task = $asTaskOp.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(); $task.Result }
function AwaitAction($op) { $task = $asTaskAct.Invoke($null, @($op)); $task.Wait() }

$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync((Resolve-Path $Pdf).Path)) ([Windows.Storage.StorageFile])
$doc = Await ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
if ($doc.PageCount -lt [Math]::Max($BlackPage, $WhitePage)) { throw "The PDF has $($doc.PageCount) page(s)." }

function RenderPage([int]$number, [int]$px) {
    $page = $doc.GetPage($number - 1)
    $opts = New-Object Windows.Data.Pdf.PdfPageRenderOptions
    $opts.DestinationWidth = $px
    $opts.DestinationHeight = $px
    $opts.BackgroundColor = [Windows.UI.Color]::FromArgb(0, 0, 0, 0)
    $stream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
    AwaitAction ($page.RenderToStreamAsync($stream, $opts))
    $net = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($stream.GetInputStreamAt(0))
    $ms = New-Object IO.MemoryStream; $net.CopyTo($ms); $ms.Position = 0
    $page.Dispose()
    return [Drawing.Bitmap]::FromStream($ms)
}

# ---- Helpers ---------------------------------------------------------------------------
function Resize([Drawing.Bitmap]$src, [int]$size) {
    # Halve in steps first so small sizes stay crisp and anti-aliased.
    $cur = $src
    while ($cur.Width / 2 -ge $size * 2) {
        $half = New-Object Drawing.Bitmap ([int]($cur.Width / 2)), ([int]($cur.Height / 2)), ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [Drawing.Graphics]::FromImage($half)
        $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingMode = 'SourceCopy'
        $g.DrawImage($cur, 0, 0, $half.Width, $half.Height); $g.Dispose()
        if ($cur -ne $src) { $cur.Dispose() }
        $cur = $half
    }
    $out = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($out)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingMode = 'SourceCopy'
    $ia = New-Object Drawing.Imaging.ImageAttributes; $ia.SetWrapMode('TileFlipXY')
    $g.DrawImage($cur, (New-Object Drawing.Rectangle 0, 0, $size, $size), 0, 0, $cur.Width, $cur.Height, 'Pixel', $ia); $g.Dispose()
    if ($cur -ne $src) { $cur.Dispose() }
    return $out
}

# A 32-bit DIB icon entry (every Windows/.NET icon reader handles these).
function DibBytes([Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) }
    }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush(); return , $ms.ToArray()
}

function PngBytes([Drawing.Bitmap]$bmp) {
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); return , $ms.ToArray()
}

function WriteIco([Drawing.Bitmap]$master, [int[]]$sizes, [string]$path) {
    $images = foreach ($sz in $sizes) {
        $b = Resize $master $sz
        [byte[]]$data = if ($sz -ge 256) { PngBytes $b } else { DibBytes $b }
        $b.Dispose()
        , @($sz, $data)
    }
    $fs = [IO.File]::Create($path); $w = New-Object IO.BinaryWriter $fs
    $w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($im in $images) {
        $sz = $im[0]; $len = $im[1].Length
        $w.Write([byte]($sz % 256)); $w.Write([byte]($sz % 256)); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$len); $w.Write([int]$offset)
        $offset += $len
    }
    foreach ($im in $images) { $w.Write([byte[]]$im[1]) }
    $w.Close()
    Write-Host "[assets] $path"
}

function WritePng([Drawing.Bitmap]$master, [int]$size, [string]$path) {
    $b = Resize $master $size; $b.Save($path, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    Write-Host "[assets] $path"
}

# ---- Generate --------------------------------------------------------------------------
$black = RenderPage $BlackPage 2048
$white = RenderPage $WhitePage 2048

$traySizes = 16, 20, 24, 32, 40, 48, 64
$appSizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

WriteIco $black $appSizes  (Join-Path $Assets "OpacityBar.ico")   # app + installer: black is the default
WriteIco $black $traySizes (Join-Path $Assets "tray-black.ico")   # light taskbar
WriteIco $white $traySizes (Join-Path $Assets "tray-white.ico")   # dark taskbar
WritePng $black 512 (Join-Path $Assets "logo-black.png")          # installer, light theme
WritePng $white 512 (Join-Path $Assets "logo-white.png")          # installer, dark theme
WritePng $black 256 (Join-Path $Docs "logo-black.png")
WritePng $white 256 (Join-Path $Docs "logo-white.png")

$black.Dispose(); $white.Dispose()
Write-Host "[assets] done. Rebuild with .\build.ps1" -ForegroundColor Green
