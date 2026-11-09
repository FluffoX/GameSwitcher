# Draws the app icon (white game controller on a violet-blue tile) and writes assets\icon.ico.
# Small sizes are stored as classic bitmaps (readable by WinForms), 256 px as PNG.
$root = Split-Path $PSScriptRoot
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$Size) {
    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $r = [math]::Max(2, $Size * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($Size - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($Size - $r * 2 - 1, $Size - $r * 2 - 1, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $Size - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0),
        (New-Object System.Drawing.Point $Size, $Size), ([System.Drawing.Color]::FromArgb(110, 70, 220)), ([System.Drawing.Color]::FromArgb(40, 140, 255))
    $g.FillPath($brush, $path)
    # Game controller glyph from the Windows icon font
    $fontName = if ((New-Object System.Drawing.Text.InstalledFontCollection).Families.Name -contains 'Segoe Fluent Icons') { 'Segoe Fluent Icons' } else { 'Segoe MDL2 Assets' }
    $font = New-Object System.Drawing.Font($fontName, [float]($Size * 0.56), [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString([string][char]0xE7FC, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($Size * 0.03), $Size, $Size), $sf)
    $g.Dispose()
    $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$Bmp) {
    $n = $Bmp.Width
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $maskRow = [int]([math]::Ceiling($n / 32) * 4)
    $w.Write([int]40); $w.Write([int]$n); $w.Write([int]($n * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($n * $n * 4 + $maskRow * $n)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) {
            $c = $Bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $w.Write((New-Object byte[] ($maskRow * $n)))
    $w.Flush(); $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -ge 256) {
        $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bytes = $ms.ToArray()
    } else { $bytes = Get-DibBytes $bmp }
    if ($s -eq 256) { $bmp.Save((Join-Path $root 'assets\icon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Bytes = $bytes }
}

New-Item -ItemType Directory -Force (Join-Path $root 'assets') | Out-Null
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$i.Bytes.Length); $w.Write([int]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $w.Write([byte[]]$i.Bytes) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $root 'assets\icon.ico'), $out.ToArray())
"assets\icon.ico written ($($out.Length) bytes)"
