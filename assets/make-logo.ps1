<#
.SYNOPSIS
    Regenerates the Voice Dictation logo: assets/logo.png, app icon (.ico) and overlay PNG.
    Run with Windows PowerShell (uses System.Drawing):  powershell -File assets\make-logo.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$appAssets = Join-Path $root 'src\VoiceDictation.App\Assets'
New-Item -ItemType Directory -Force $appAssets | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Logo([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Tile: teal -> emerald diagonal gradient (Voice Assistant uses indigo -> violet).
    $tile = New-RoundedPath (6 * $s) (6 * $s) (244 * $s) (244 * $s) (56 * $s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(255, 13, 148, 136)), ([System.Drawing.Color]::FromArgb(255, 16, 185, 129))
    $g.FillPath($grad, $tile)

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $stroke = [Math]::Max(1.5, 14 * $s)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $stroke
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'

    # Microphone capsule.
    $capsule = New-RoundedPath (100 * $s) (48 * $s) (56 * $s) (104 * $s) (28 * $s)
    $g.FillPath($white, $capsule)
    # Stand arc, stem, base.
    $g.DrawArc($pen, 76 * $s, 92 * $s, 104 * $s, 88 * $s, 0, 180)
    $g.DrawLine($pen, 128 * $s, 180 * $s, 128 * $s, 204 * $s)
    $g.DrawLine($pen, 100 * $s, 206 * $s, 156 * $s, 206 * $s)

    if ($size -ge 32) {
        # Sound waves on both sides.
        $wave = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(230, 255, 255, 255)), ([Math]::Max(1.5, 10 * $s))
        $wave.StartCap = 'Round'; $wave.EndCap = 'Round'
        $g.DrawArc($wave, 34 * $s, 66 * $s, 60 * $s, 92 * $s, 120, 120)
        $g.DrawArc($wave, 162 * $s, 66 * $s, 60 * $s, 92 * $s, -60, 120)
        $wave.Dispose()
    }

    $pen.Dispose(); $white.Dispose(); $grad.Dispose(); $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    # The leading comma stops PowerShell from unrolling the byte array into the pipeline.
    return , $ms.ToArray()
}

# README / marketing logo.
$big = New-Logo 512
$big.Save((Join-Path $PSScriptRoot 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()

# Overlay pill logo.
$small = New-Logo 64
$small.Save((Join-Path $appAssets 'logo-64.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$small.Dispose()

# Multi-resolution .ico with PNG-compressed entries.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = New-Object System.Collections.Generic.List[byte[]]
foreach ($size in $sizes) { $b = New-Logo $size; $images.Add((Get-PngBytes $b)); $b.Dispose() }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $appAssets 'app.ico'), $ico.ToArray())
Write-Host "Logo written to assets\logo.png and $appAssets"
