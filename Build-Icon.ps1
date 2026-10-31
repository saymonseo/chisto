$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$assetDir = Join-Path $PSScriptRoot 'Assets'
$null = New-Item -ItemType Directory -Path $assetDir -Force
$images = @()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $ctx = $visual.RenderOpen()
    $ctx.PushTransform([Windows.Media.ScaleTransform]::new($size/256.0,$size/256.0))
    $background = [Windows.Media.LinearGradientBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#182338'),[Windows.Media.ColorConverter]::ConvertFromString('#080D17'),45)
    $ctx.DrawRoundedRectangle($background,$null,[Windows.Rect]::new(5,5,246,246),56,56)
    $outline = [Windows.Media.Pen]::new([Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#405676')),3)
    $ctx.DrawRoundedRectangle($null,$outline,[Windows.Rect]::new(8,8,240,240),53,53)
    $arc = [Windows.Media.Geometry]::Parse('M 175,76 C 157,59 119,56 94,75 C 62,98 61,148 88,173 C 112,197 149,198 176,179 L 157,155 C 140,166 119,163 108,150 C 93,132 97,109 112,98 C 126,88 146,88 158,99 Z')
    $blue = [Windows.Media.LinearGradientBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#91CEFF'),[Windows.Media.ColorConverter]::ConvertFromString('#4988F5'),90)
    $ctx.DrawGeometry($blue,$null,$arc)
    $spark = [Windows.Media.Geometry]::Parse('M 192,53 L 199,73 L 219,80 L 199,87 L 192,107 L 185,87 L 165,80 L 185,73 Z')
    $ctx.DrawGeometry([Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#A7F3E1')),$null,$spark)
    $ctx.Pop();$ctx.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = [IO.MemoryStream]::new();$encoder.Save($memory);$bytes = $memory.ToArray();$memory.Dispose()
    $images += [pscustomobject]@{ Size=$size; Bytes=$bytes }
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assetDir 'app.png'),$bytes) }
}
$stream = [IO.File]::Create((Join-Path $assetDir 'app.ico'))
$writer = [IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dimension = if ($img.Size -eq 256) { 0 } else { $img.Size }
    $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
    $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$img.Bytes.Length);$writer.Write([uint32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $writer.Write([byte[]]$img.Bytes) }
$writer.Dispose();$stream.Dispose()
Write-Output 'Created vector-rendered icon: 16 through 256 px.'
