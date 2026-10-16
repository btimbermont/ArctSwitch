# Generates ArctSwitch/app.ico: the same headset glyph as the "headset on" tray icon
# (TrayApp.MakeHeadsetIcon, drawn on a 32x32 grid), rendered at each standard icon size.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$color = [System.Drawing.Color]::FromArgb(255, 82, 0)
$out = Join-Path $PSScriptRoot '..\ArctSwitch\app.ico'

$images = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform($size / 32.0, $size / 32.0)
    $pen = New-Object System.Drawing.Pen $color, 3.5
    $g.DrawArc($pen, 5, 4, 22, 24, 180, 180)
    $brush = New-Object System.Drawing.SolidBrush $color
    $g.FillRectangle($brush, 3, 15, 7, 13)
    $g.FillRectangle($brush, 22, 15, 7, 13)
    $g.Dispose(); $pen.Dispose(); $brush.Dispose()

    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # Large sizes are stored PNG-compressed (Vista+ format).
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Small sizes as classic 32-bit DIBs for maximum compatibility:
        # BITMAPINFOHEADER (height doubled for the AND mask), bottom-up BGRA rows, then an all-zero AND mask.
        $bw = New-Object System.IO.BinaryWriter $ms
        $maskRow = [int]([math]::Ceiling($size / 32.0) * 4)
        $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $bw.Write([uint32]($size * $size * 4 + $maskRow * $size))
        $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $bw.Write((New-Object byte[] ($maskRow * $size)))
        $bw.Flush()
    }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container: 6-byte header, 16-byte directory entry per image, then the image data.
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))   # 256 is stored as 0
    $w.Write([byte]0); $w.Write([byte]0)                     # palette, reserved
    $w.Write([uint16]1); $w.Write([uint16]32)                # planes, bits per pixel
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Output "Wrote $((Resolve-Path $out).Path)"
