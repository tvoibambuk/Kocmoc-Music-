$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase,PresentationCore,PresentationFramework
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'
$drawing = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $assets 'mark.xaml')))
$frames = @()
foreach ($size in 16,20,24,32,40,48,64,128,256) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.PushTransform((New-Object Windows.Media.ScaleTransform ($size/128),($size/128)))
    $context.DrawDrawing($drawing)
    $context.Pop()
    $context.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size,$size,96,96,([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    $frames += [pscustomobject]@{ Size = $size; Data = $bytes }
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assets 'velora.png'), $bytes) }
}
$writer = New-Object IO.BinaryWriter ([IO.File]::Create((Join-Path $assets 'velora.ico')))
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = [byte]($frame.Size % 256)
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Data.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
} finally { $writer.Dispose() }
