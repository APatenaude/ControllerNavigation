#Requires -Version 5.1
<#
.SYNOPSIS
    Draws the app icon (a controller on a blue tile) and writes src\ControllerNavigation\app.ico.

.DESCRIPTION
    One PNG image per size (16, 20, 24, 32, 40, 48, 64, 256) inside the .ico, as Windows reads
    them. The tray icons are drawn at run time by TrayIcons (TrayApp.cs) with the same shapes; keep
    the two in step when the design changes.
#>
param([string]$Out = (Join-Path $PSScriptRoot '..\src\ControllerNavigation\app.ico'))

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0
    $tile = New-RoundedPath 0 0 $size $size (7 * $s)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 37, 99, 235))), $tile)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 37, 99, 235))
    # body and the two grips
    $g.FillPath($white, (New-RoundedPath (5 * $s) (10 * $s) (22 * $s) (11 * $s) (5.5 * $s)))
    $g.FillEllipse($white, 4 * $s, 13 * $s, 9 * $s, 11 * $s)
    $g.FillEllipse($white, 19 * $s, 13 * $s, 9 * $s, 11 * $s)
    # D-pad and face buttons, cut out in the tile's blue
    $g.FillRectangle($blue, 8 * $s, 14.5 * $s, 6 * $s, 2 * $s)
    $g.FillRectangle($blue, 10 * $s, 12.5 * $s, 2 * $s, 6 * $s)
    $g.FillEllipse($blue, 20 * $s, 13 * $s, 2.6 * $s, 2.6 * $s)
    $g.FillEllipse($blue, 22.4 * $s, 15.4 * $s, 2.6 * $s, 2.6 * $s)
    $g.FillEllipse($blue, 17.6 * $s, 15.4 * $s, 2.6 * $s, 2.6 * $s)
    $g.FillEllipse($blue, 20 * $s, 17.8 * $s, 2.6 * $s, 2.6 * $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = $sizes | ForEach-Object { , (New-IconPng $_) }
$file = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $file
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
[System.IO.File]::WriteAllBytes($Out, $file.ToArray())
Write-Host "Wrote $Out"
