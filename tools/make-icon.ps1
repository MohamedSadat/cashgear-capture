<#
.SYNOPSIS
  Draws the app icon (a blue rounded square with white viewfinder corners) and writes a multi-size .ico.
.DESCRIPTION
  Each size is drawn separately with its corners snapped to whole pixels, so the small sizes used by the
  notification area stay sharp. Sizes below 256 are stored as 32-bit DIBs (readable by every Windows API);
  256 is stored as PNG, as Windows expects.
.EXAMPLE
  powershell -File tools\make-icon.ps1
#>
param([string]$Output = (Join-Path $PSScriptRoot '..\src\CashGear.Capture.App\Assets\app.ico'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

function New-IconImage([int]$n) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()

    $top = [System.Windows.Media.Color]::FromRgb(0x3B, 0x8C, 0xF0)
    $bottom = [System.Windows.Media.Color]::FromRgb(0x17, 0x58, 0xBE)
    $fill = [System.Windows.Media.LinearGradientBrush]::new($top, $bottom, 90.0)
    $margin = [Math]::Max(0.5, $n * 0.03)
    $radius = $n * 0.2
    $dc.DrawRoundedRectangle($fill, $null, [System.Windows.Rect]::new($margin, $margin, $n - 2 * $margin, $n - 2 * $margin), $radius, $radius)

    # Viewfinder corners, snapped to pixels.
    $white = [System.Windows.Media.Brushes]::White
    $inset = [Math]::Round($n * 0.21)
    $arm = [Math]::Max(2, [Math]::Round($n * 0.22))
    $t = [Math]::Max(1, [Math]::Round($n * 0.09))
    $far = $n - $inset
    foreach ($corner in @(@($inset, $inset, 1, 1), @($far, $inset, -1, 1), @($inset, $far, 1, -1), @($far, $far, -1, -1))) {
        $x = $corner[0]; $y = $corner[1]; $dx = $corner[2]; $dy = $corner[3]
        $hx = if ($dx -gt 0) { $x } else { $x - $arm }
        $hy = if ($dy -gt 0) { $y } else { $y - $t }
        $vx = if ($dx -gt 0) { $x } else { $x - $t }
        $vy = if ($dy -gt 0) { $y } else { $y - $arm }
        $dc.DrawRectangle($white, $null, [System.Windows.Rect]::new($hx, $hy, $arm, $t))
        $dc.DrawRectangle($white, $null, [System.Windows.Rect]::new($vx, $vy, $t, $arm))
    }

    # The "lens": a dot in the middle.
    $r = [Math]::Max(1.6, $n * 0.11)
    $dc.DrawEllipse($white, $null, [System.Windows.Point]::new($n / 2, $n / 2), $r, $r)
    $dc.Close()

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($n, $n, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

function Get-Bgra($bitmap, [int]$n) {
    # Straight (non-premultiplied) BGRA, top-down.
    $converted = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new($bitmap, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $pixels = New-Object byte[] ($n * $n * 4)
    $converted.CopyPixels($pixels, $n * 4, 0)
    return , $pixels
}

function Get-Dib([byte[]]$bgra, [int]$n) {
    $stream = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($stream)
    $w.Write([int]40); $w.Write([int]$n); $w.Write([int]($n * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($n * $n * 4)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($row = $n - 1; $row -ge 0; $row--) { $w.Write($bgra, $row * $n * 4, $n * 4) } # bottom-up
    $maskStride = [int]([Math]::Ceiling($n / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskStride * $n))) # AND mask unused: alpha decides
    $w.Flush()
    return , $stream.ToArray()
}

function Get-Png($bitmap) {
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return , $stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($n in $sizes) {
    $bitmap = New-IconImage $n
    $data = if ($n -ge 256) { Get-Png $bitmap } else { Get-Dib (Get-Bgra $bitmap $n) $n }
    [pscustomobject]@{ Size = $n; Data = $data }
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $w.Write([byte]$dimension); $w.Write([byte]$dimension); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$image.Data.Length); $w.Write([int]$offset)
    $offset += $image.Data.Length
}
foreach ($image in $images) { $w.Write($image.Data) }
$w.Flush()

New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
[System.IO.File]::WriteAllBytes($Output, $out.ToArray())
"Wrote $Output ($($sizes -join ', ') px, $($out.Length) bytes)"
