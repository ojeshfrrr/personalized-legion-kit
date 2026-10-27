param([string]$OutputDirectory)
# Detached native resource rendering only; no application, settings or hardware services.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$directory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($directory) | Out-Null
# Detached verifier resolves the same PNGs locally; compiled pack-URI loading
# is checked separately by ReadOnlyUiChecks against the production assembly.
$markup = [IO.File]::ReadAllText((Join-Path $root 'LenovoLegionToolkit.WPF/Assets/Branding/Identity.xaml'))
$prefix = ([Uri]((Join-Path $root 'assets/branding/') )).AbsoluteUri
$markup = $markup.Replace('pack://application:,,,/Project%20Legion;component/Assets/Branding/', $prefix)
$reader = [Xml.XmlReader]::Create((New-Object IO.StringReader($markup)))
try { $resources = [Windows.Markup.XamlReader]::Load($reader) } finally { $reader.Close() }
$results = @()
foreach ($key in $resources.Keys) {
    $image = $resources[$key]
    if (!$image.IsFrozen) { throw "Unfrozen identity: $key" }
    $sizes = if ($key -like '*Wordmark') { @(88,240) } else { @(16,32,64) }
    foreach ($width in $sizes) {
        $height = [int][Math]::Ceiling($width * $image.Height / $image.Width)
        $visual = New-Object Windows.Media.DrawingVisual
        $context = $visual.RenderOpen()
        $context.DrawImage($image, (New-Object Windows.Rect(0,0,$width,$height)))
        $context.Close()
        $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($width,$height,96,96,([Windows.Media.PixelFormats]::Pbgra32))
        $bitmap.Render($visual)
        $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = [IO.File]::Create((Join-Path $directory "$key-$width.png"))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
        $results += @{ resource=$key; width=$width; height=$height; frozen=$image.IsFrozen }
    }
}
$results | ConvertTo-Json | Set-Content (Join-Path $directory 'vector-checks.json') -Encoding UTF8
Write-Output "$($results.Count) detached PNG logo renders passed. No application launched."
