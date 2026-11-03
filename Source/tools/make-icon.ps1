# Genera ui\app.ico (icona multi-risoluzione) disegnandola con WPF.
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$out = Join-Path $root 'ui\app.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()

foreach ($s in $sizes) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $grad = New-Object System.Windows.Media.LinearGradientBrush(
        [System.Windows.Media.Color]::FromRgb(0x6F, 0x9B, 0xFF), [System.Windows.Media.Color]::FromRgb(0x7B, 0x5C, 0xFF),
        (New-Object System.Windows.Point(0, 0)), (New-Object System.Windows.Point(1, 1)))
    $pad = [Math]::Max(0.5, $s * 0.03)
    $radius = $s * 0.24
    $side = $s - 2 * $pad
    $rect = New-Object System.Windows.Rect -ArgumentList $pad, $pad, $side, $side
    $dc.DrawRoundedRectangle($grad, $null, $rect, $radius, $radius)
    $face = New-Object System.Windows.Media.Typeface('Segoe Fluent Icons')
    $text = New-Object System.Windows.Media.FormattedText([string][char]0xE74C, [Globalization.CultureInfo]::InvariantCulture,
        [System.Windows.FlowDirection]::LeftToRight, $face, ($s * 0.54), [System.Windows.Media.Brushes]::White, 1.0)
    $dc.DrawText($text, (New-Object System.Windows.Point((($s - $text.Width) / 2), (($s - $text.Height) / 2))))
    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($s, $s, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    $pngs += , $ms.ToArray()
    if ($s -eq 256 -and $env:ICON_PREVIEW) { [System.IO.File]::WriteAllBytes($env:ICON_PREVIEW, $ms.ToArray()) }
}

# Formato ICO con immagini PNG (supportato da Windows Vista in poi)
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"Icona creata: $out ($((Get-Item $out).Length) byte)"
