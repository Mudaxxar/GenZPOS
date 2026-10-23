param([string]$OutIco, [string]$OutPng)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Draws the icon on a 256-unit canvas, scaled to $size pixels.
function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # Background: rounded square, indigo -> violet
    $bg = RoundedRect 8 8 240 240 52
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 8, 8), (New-Object System.Drawing.PointF 248, 248),
        [System.Drawing.Color]::FromArgb(255, 67, 64, 210), [System.Drawing.Color]::FromArgb(255, 124, 58, 237))
    $g.FillPath($bgBrush, $bg)

    # Paint stroke the roller has just painted (warm gradient band, bottom-left)
    $strokeBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 30, 146), (New-Object System.Drawing.PointF 140, 226),
        [System.Drawing.Color]::FromArgb(255, 255, 183, 77), [System.Drawing.Color]::FromArgb(255, 244, 63, 94))
    $g.FillPath($strokeBrush, (RoundedRect 30 146 112 44 16))   # painted band
    $g.FillPath($strokeBrush, (RoundedRect 46 170 22 56 11))    # long drip
    $g.FillPath($strokeBrush, (RoundedRect 86 170 20 38 10))    # short drip
    $g.FillEllipse($strokeBrush, 116, 196, 16, 16)              # droplet

    # Roller cylinder (white frame + paint-coloured sleeve)
    $sleeveOuter = RoundedRect 40 46 150 62 20
    $g.FillPath([System.Drawing.Brushes]::White, $sleeveOuter)
    $sleeve = RoundedRect 50 56 130 42 13
    $sleeveBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 50, 56), (New-Object System.Drawing.PointF 180, 98),
        [System.Drawing.Color]::FromArgb(255, 255, 183, 77), [System.Drawing.Color]::FromArgb(255, 244, 63, 94))
    $g.FillPath($sleeveBrush, $sleeve)

    # Handle frame: out from the roller, down, back to centre, then the grip
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 14
    $pen.LineJoin = 'Round'
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF 190, 77),
        (New-Object System.Drawing.PointF 214, 77),
        (New-Object System.Drawing.PointF 214, 132),
        (New-Object System.Drawing.PointF 168, 132),
        (New-Object System.Drawing.PointF 168, 160))
    $g.DrawLines($pen, $pts)

    $grip = RoundedRect 154 156 28 76 13
    $g.FillPath([System.Drawing.Brushes]::White, $grip)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = Draw $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($sz -eq 256 -and $OutPng) { $bmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    $pngs += , @($sz, $ms.ToArray())
}

# ICO container with PNG-compressed entries (supported since Windows Vista)
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($e in $pngs) {
    $sz = $e[0]; $data = $e[1]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($e in $pngs) { $w.Write([byte[]]$e[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes($OutIco, $out.ToArray())
"Wrote $OutIco ($($out.Length) bytes)"
