param([Parameter(Mandatory = $true)][string]$Manifest)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$spec = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
$root = [IO.Path]::GetFullPath($spec.root)

foreach ($job in $spec.jobs) {
    $target = [IO.Path]::GetFullPath($job.path)
    if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Asset outside workspace: $target" }
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.PushTransform([Windows.Media.ScaleTransform]::new($job.width / $job.canvasWidth, $job.height / $job.canvasHeight))
    foreach ($shape in $job.shapes) {
        $geometry = [Windows.Media.Geometry]::Parse('F0 ' + $shape.path)
        $geometry.Freeze()
        $brush = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($shape.fill))
        $brush.Freeze()
        if ($shape.transform) {
            $t = $shape.transform
            $context.PushTransform([Windows.Media.MatrixTransform]::new($t[0], 0, 0, $t[1], $t[2], $t[3]))
        }
        $context.DrawGeometry($brush, $null, $geometry)
        if ($shape.transform) { $context.Pop() }
    }
    $context.Pop()
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($job.width, $job.height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create($target)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

# PNG-compressed 32-bit entries are supported by Windows Vista and later.
$images = @($spec.iconSizes | ForEach-Object { ,([IO.File]::ReadAllBytes((Join-Path $root "assets\branding\icons\icon-$_.png"))) })
$buffer = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($buffer)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    for ($i = 0; $i -lt $images.Count; $i++) {
        $size = [int]$spec.iconSizes[$i]
        $dimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
    $writer.Flush()
    foreach ($relative in @('assets\branding\project-legion.ico', 'LenovoLegionToolkit.WPF\Assets\ProjectLegion.ico', 'InnoDependencies\Images\setup_icon.ico')) {
        [IO.File]::WriteAllBytes((Join-Path $root $relative), $buffer.ToArray())
    }
} finally { $writer.Dispose(); $buffer.Dispose() }
