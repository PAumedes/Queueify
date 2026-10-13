# Renders the Queueify icon (a record sliding out of a magazine page) at every icon size
# and packs them into Assets\Queueify.ico, plus PNGs. The master vector is Assets\Queueify.svg;
# the shapes below use the same coordinates, so keep both in step when changing the design.
# Run with Windows PowerShell: powershell -File tools\make-icon.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot '..\Queueify\Assets'))

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Color([string]$hex, [int]$alpha = 255) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    return [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B)
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # Background tile: violet -> pink
    $bg = New-RoundedPath 8 8 240 240 54
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 8, 8), (New-Object System.Drawing.PointF 248, 248), (Color '#6C4BFF'), (Color '#FF4D8D')
    $g.FillPath($grad, $bg)

    # Record
    $disc = Color '#15121C'
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $disc), 90, 54, 148, 148)
    if ($size -ge 40) {
        $groove = New-Object System.Drawing.Pen (Color '#3A3348'), 3
        foreach ($r in 62, 52, 42) { $g.DrawEllipse($groove, 164 - $r, 128 - $r, 2 * $r, 2 * $r) }
    }
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Color '#FFC0DA')), 138, 102, 52, 52)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $disc), 159, 123, 10, 10)

    # Magazine page in front of the record
    if ($size -ge 32) {
        $shadow = New-RoundedPath 40 62 118 146 14
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 20, 10, 40))), $shadow)
    }
    $page = New-RoundedPath 34 54 118 146 14
    $g.FillPath([System.Drawing.Brushes]::White, $page)
    $g.FillPath((New-Object System.Drawing.SolidBrush (Color '#6C4BFF')), (New-RoundedPath 52 74 70 18 5))
    if ($size -ge 24) {
        $line = New-Object System.Drawing.SolidBrush (Color '#D9D3EA')
        foreach ($l in @(@(104, 82), @(122, 74), @(140, 82), @(158, 58))) {
            $g.FillPath($line, (New-RoundedPath 52 $l[0] $l[1] 9 4))
        }
    }

    $g.Dispose()
    return $bmp
}

# Classic 32-bit DIB entry (BGRA, bottom-up, followed by a 1-bit AND mask). Every icon reader
# understands these; PNG entries are only safe for the 256px size.
function To-Dib([System.Drawing.Bitmap]$bmp) {
    $n = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$n); $w.Write([int32]($n * 2))
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
    $maskStride = [int]([Math]::Ceiling($n / 32.0) * 4)
    $w.Write([uint32]($n * $n * 4 + $maskStride * $n))
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $w.Write((New-Object byte[] ($maskStride * $n)))
    $w.Flush()
    return , $ms.ToArray()   # leading comma: return the byte[] itself, not its unrolled bytes
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$pngs = @()
foreach ($size in $sizes) {
    $bmp = Render $size
    if ($size -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , @($size, $ms.ToArray())
    } else {
        $pngs += , @($size, (To-Dib $bmp))
    }
    $bmp.Dispose()
}

# ICO container with PNG-compressed entries
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($entry in $pngs) {
    $dim = if ($entry[0] -ge 256) { 0 } else { $entry[0] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$entry[1].Length); $w.Write([uint32]$offset)
    $offset += $entry[1].Length
}
foreach ($entry in $pngs) { $w.Write($entry[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'Queueify.ico'), $ico.ToArray())

foreach ($size in 64, 256) {
    $bmp = Render $size
    $bmp.Save((Join-Path $OutDir "Queueify-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Write-Host "Icon written to $OutDir"
