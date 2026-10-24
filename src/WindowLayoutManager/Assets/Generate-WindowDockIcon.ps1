<#
.SYNOPSIS
  Generates Assets\WindowDock.ico (16/20/24/32/48/64/128/256) for 창자리 (WindowDock).

.DESCRIPTION
  Deterministic, offline, no dependencies beyond the built-in System.Drawing (Windows).
  Draws the same geometry as WindowDock.svg on a 256-unit grid scaled to each size:
  navy/blue rounded tile, two white window rectangles, a small teal docking corner. No text.
  Sizes < 256 are stored as 32-bit BGRA DIB entries (with an all-zero AND mask; alpha is used);
  the 256 entry is stored as PNG, which Windows and System.Drawing.Icon both accept.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Generate-WindowDockIcon.ps1
  # writes .\WindowDock.ico next to this script (overwrites only that file)

.EXAMPLE
  powershell -NoProfile -File .\Generate-WindowDockIcon.ps1 -OutFile C:\temp\WindowDock.ico -PreviewDir C:\temp\png
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot 'WindowDock.ico'),
    [string]$PreviewDir = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single]([Math]::Min($r * 2, [Math]::Min($w, $h)))
    if ($d -le 0.5) { $p.AddRectangle((New-Object System.Drawing.RectangleF $x, $y, $w, $h)); return $p }
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $k = [single]($s / 256.0)

        # Tile
        $tile = New-RoundedPath (12 * $k) (12 * $k) (232 * $k) (232 * $k) (52 * $k)
        $rect = New-Object System.Drawing.RectangleF ([single](12 * $k)), ([single](12 * $k)), ([single](232 * $k)), ([single](232 * $k))
        $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, `
            ([System.Drawing.Color]::FromArgb(255, 0x1E, 0x3A, 0x8A)), `
            ([System.Drawing.Color]::FromArgb(255, 0x25, 0x63, 0xEB)), `
            ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $g.FillPath($grad, $tile)
        $grad.Dispose(); $tile.Dispose()

        # Back window (translucent white)
        $back = New-RoundedPath (48 * $k) (56 * $k) (116 * $k) (88 * $k) (10 * $k)
        $bb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(184, 255, 255, 255))
        $g.FillPath($bb, $back); $bb.Dispose(); $back.Dispose()

        # Front window (white) + light title bar
        $front = New-RoundedPath (88 * $k) (104 * $k) (120 * $k) (92 * $k) (10 * $k)
        $fb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        $g.FillPath($fb, $front); $fb.Dispose()
        if ($s -ge 24) {
            $oldClip = $g.Clip
            $g.SetClip($front)
            $tb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xBF, 0xDB, 0xFE))
            $g.FillRectangle($tb, [single](88 * $k), [single](104 * $k), [single](120 * $k), [single](20 * $k))
            $tb.Dispose()
            $g.Clip = $oldClip
        }
        $front.Dispose()

        # Teal docking corner (bracket), kept at least ~1.6 px wide at tiny sizes
        $penW = [single]([Math]::Max(16 * $k, 1.6))
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x14, 0xB8, 0xA6)), $penW
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $pts = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF ([single](222 * $k)), ([single](158 * $k))),
            (New-Object System.Drawing.PointF ([single](222 * $k)), ([single](210 * $k))),
            (New-Object System.Drawing.PointF ([single](170 * $k)), ([single](210 * $k))))
        $g.DrawLines($pen, $pts)
        $pen.Dispose()
    }
    finally { $g.Dispose() }
    return $bmp
}

function Get-BgraBytes([System.Drawing.Bitmap]$bmp) {
    $r = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
    $data = $bmp.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $buf = New-Object byte[] ($stride * $bmp.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
        return @{ Bytes = $buf; Stride = $stride }
    }
    finally { $bmp.UnlockBits($data) }
}

function Get-DibEntry([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $px = Get-BgraBytes $bmp
    $rowBytes = $s * 4
    $maskRow = [int]([Math]::Floor(($s + 31) / 32) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER (height doubled: XOR image + AND mask)
    $w.Write([int32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int32]0)
    $w.Write([int32]($rowBytes * $s + $maskRow * $s))
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([int32]0); $w.Write([int32]0)
    # Pixel rows bottom-up, BGRA (Format32bppArgb memory order), non-premultiplied
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($px.Bytes, $y * $px.Stride, $rowBytes) }
    # AND mask: all zero (alpha channel defines transparency)
    $zero = New-Object byte[] ($maskRow * $s)
    $w.Write($zero)
    $w.Flush()
    return $ms.ToArray()
}

function Get-PngEntry([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

$entries = @()
foreach ($s in $sizes) {
    $bmp = Render-Icon $s
    try {
        if ($PreviewDir) {
            New-Item -ItemType Directory -Force -Path $PreviewDir | Out-Null
            $bmp.Save((Join-Path $PreviewDir "WindowDock-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        $bytes = if ($s -ge 256) { Get-PngEntry $bmp } else { Get-DibEntry $bmp }
        $entries += , @{ Size = $s; Bytes = $bytes }
    }
    finally { $bmp.Dispose() }
}

# ICONDIR + ICONDIRENTRY[] + image data
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int32]$e.Bytes.Length); $bw.Write([int32]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $bw.Write([byte[]]$e.Bytes) }
$bw.Flush()

$dir = Split-Path -Parent $OutFile
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
$hash = (Get-FileHash -LiteralPath $OutFile -Algorithm SHA256).Hash
Write-Output ("Wrote {0} ({1} bytes, sizes {2}) SHA256 {3}" -f $OutFile, $out.Length, ($sizes -join ','), $hash)
