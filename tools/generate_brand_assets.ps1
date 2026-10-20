 $ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Project Legion visual identity settings.
$script:BrandName = 'Project Legion'
$script:BrandMonogram = 'PL'
$script:Background = [System.Drawing.ColorTranslator]::FromHtml('#000000')
$script:Tile = [System.Drawing.ColorTranslator]::FromHtml('#0B0D10')
$script:Accent = [System.Drawing.ColorTranslator]::FromHtml('#FF5263')
$script:Foreground = [System.Drawing.Color]::White

function Draw-Tile($graphics, [int]$x, [int]$y, [int]$size) {
    $radius = [Math]::Max(4, [int]($size * 0.16))
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($x, $y, $radius * 2, $radius * 2, 180, 90)
    $path.AddArc($x + $size - $radius * 2, $y, $radius * 2, $radius * 2, 270, 90)
    $path.AddArc($x + $size - $radius * 2, $y + $size - $radius * 2, $radius * 2, $radius * 2, 0, 90)
    $path.AddArc($x, $y + $size - $radius * 2, $radius * 2, $radius * 2, 90, 90)
    $path.CloseFigure()

    $fill = [System.Drawing.SolidBrush]::new($script:Tile)
    $stroke = [System.Drawing.Pen]::new($script:Accent, [Math]::Max(2, $size * 0.025))
    $graphics.FillPath($fill, $path)
    $graphics.DrawPath($stroke, $path)
    $font = [System.Drawing.Font]::new('Segoe UI Semibold', [Math]::Max(8, $size * 0.30), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $brush = [System.Drawing.SolidBrush]::new($script:Foreground)
    $graphics.DrawString($script:BrandMonogram, $font, $brush, [System.Drawing.RectangleF]::new($x, $y, $size, $size), $format)

    $brush.Dispose(); $format.Dispose(); $font.Dispose(); $stroke.Dispose(); $fill.Dispose(); $path.Dispose()
}

function New-BrandImage([int]$width, [int]$height, [bool]$wordmark = $false) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear($script:Background)

    if ($wordmark -and $width -gt $height) {
        $tileSize = [int]($height * 0.72)
        $margin = [int](($height - $tileSize) / 2)
        Draw-Tile $graphics $margin $margin $tileSize
        $fontSize = [Math]::Max(13, [int]($height * 0.30))
        $font = [System.Drawing.Font]::new('Segoe UI Semibold', $fontSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $brush = [System.Drawing.SolidBrush]::new($script:Foreground)
        $graphics.DrawString($script:BrandName, $font, $brush, [single]($margin + $tileSize + $height * 0.20), [single](($height - $fontSize) / 2))
        $brush.Dispose(); $font.Dispose()
    }
    else {
        $tileSize = [int]([Math]::Min($width, $height) * 0.82)
        $x = [int](($width - $tileSize) / 2)
        $y = [int](($height - $tileSize) / 2)
        Draw-Tile $graphics $x $y $tileSize
    }

    $graphics.Dispose()
    return $bitmap
}

function Save-BrandPng([string]$path, [int]$width, [int]$height, [bool]$wordmark = $false) {
    $bitmap = New-BrandImage $width $height $wordmark
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Save-IcoFromPng([string]$pngPath, [string]$icoPath) {
    $png = [System.IO.File]::ReadAllBytes($pngPath)
    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]1)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$png.Length)
    $writer.Write([UInt32]22)
    $writer.Write($png)
    [System.IO.File]::WriteAllBytes($icoPath, $stream.ToArray())
    $writer.Dispose(); $stream.Dispose()
}

$root = Split-Path -Parent $PSScriptRoot
$iconPng = Join-Path $root 'LenovoLegionToolkit.WPF\Assets\ProjectLegion.png'
$iconIco = Join-Path $root 'LenovoLegionToolkit.WPF\Assets\ProjectLegion.ico'
Save-BrandPng $iconPng 256 256
Save-IcoFromPng $iconPng $iconIco
Save-BrandPng (Join-Path $root 'assets\logo.png') 512 512
Save-BrandPng (Join-Path $root 'InnoDependencies\Images\logo.png') 380 100 $true
Copy-Item -LiteralPath $iconIco -Destination (Join-Path $root 'InnoDependencies\Images\setup_icon.ico') -Force

$imageRoot = Join-Path $root 'LenovoLegionToolkit.LampArray\Images'
foreach ($file in Get-ChildItem -LiteralPath $imageRoot -Filter '*.png') {
    $name = $file.Name
    if ($name -like 'SplashScreen*') { $w = 1240; $h = 600; $wordmark = $true }
    elseif ($name -like 'Wide310x150Logo*') { $w = 620; $h = 300; $wordmark = $true }
    elseif ($name -like 'Square150x150Logo*') { $w = 300; $h = 300; $wordmark = $false }
    elseif ($name -like 'Square44x44Logo.scale-200*') { $w = 88; $h = 88; $wordmark = $false }
    elseif ($name -match 'targetsize-(\d+)') { $w = [int]$Matches[1]; $h = $w; $wordmark = $false }
    elseif ($name -eq 'StoreLogo.png') { $w = 50; $h = 50; $wordmark = $false }
    elseif ($name -like 'LockScreenLogo*') { $w = 96; $h = 96; $wordmark = $false }
    else { continue }

    Save-BrandPng $file.FullName $w $h $wordmark
}
