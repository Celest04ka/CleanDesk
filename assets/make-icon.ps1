# Draws assets\icon.ico (a monitor with a down arrow and a taskbar) with System.Drawing.
# Only needed if you want to change the icon: build.ps1 uses the committed icon.ico.
# Usage: powershell -ExecutionPolicy Bypass -File assets\make-icon.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [float](2 * $r)
    $p.AddArc($x, $y, $d, $d, [float]180, [float]90)
    $p.AddArc([float]($x + $w - $d), $y, $d, $d, [float]270, [float]90)
    $p.AddArc([float]($x + $w - $d), [float]($y + $h - $d), $d, $d, [float]0, [float]90)
    $p.AddArc($x, [float]($y + $h - $d), $d, $d, [float]90, [float]90)
    $p.CloseFigure()
    $p
}

# Drawn on a 32x32 grid and scaled to the requested size.
function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform([float]($s / 32.0), [float]($s / 32.0))

    $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 38, 42, 51))
    $g.FillPath($dark, (New-RoundRect 1 3 30 21 3.5))                 # bezel
    $g.FillRectangle($dark, [float]13, [float]23, [float]6, [float]4.5) # neck
    $g.FillPath($dark, (New-RoundRect 8.5 26.5 15 3 1.5))             # stand

    $screen = New-RoundRect 3.2 4.8 25.6 17.4 1.8
    $rect = New-Object System.Drawing.RectangleF 3.2, 4.8, 25.6, 17.4
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255, 96, 170, 255)),
        ([System.Drawing.Color]::FromArgb(255, 40, 100, 220)),
        ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($grad, $screen)

    # taskbar at the bottom of the screen
    $g.SetClip($screen)
    $bar = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 20, 24, 32))
    $g.FillRectangle($bar, [float]3.2, [float]17.6, [float]25.6, [float]4.6)
    $g.ResetClip()
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
    foreach ($x in 10.7, 14.7, 18.7) { $g.FillRectangle($white, [float]$x, [float]18.9, [float]2.6, [float]2.2) }

    # down arrow: "the taskbar goes away"
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float]2.4)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF 11.5, 8.5),
        (New-Object System.Drawing.PointF 16, 13),
        (New-Object System.Drawing.PointF 20.5, 8.5)))

    $g.Dispose()
    $bmp
}

# ICO: frames up to 64 px are 32-bit BMPs with alpha, the 256 px frame is a PNG.
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2))
        $bw.Write([int16]1); $bw.Write([int16]32)
        for ($i = 0; $i -lt 6; $i++) { $bw.Write([int]0) }
        $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $s, $s),
            [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $raw = [byte[]]::new($data.Stride * $s)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
        $bmp.UnlockBits($data)
        for ($y = $s - 1; $y -ge 0; $y--) { $bw.Write($raw, $y * $data.Stride, $s * 4) }
        $bw.Write([byte[]]::new([int]([math]::Ceiling($s / 32) * 4 * $s)))   # AND mask
        $bw.Flush()
    }
    $images.Add($ms.ToArray())
    $bmp.Dispose()
}

$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]$images[$i].Length); $bw.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'icon.ico'), $ico.ToArray())
Write-Host 'Wrote assets\icon.ico'
