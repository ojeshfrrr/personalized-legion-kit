param([switch]$Preview)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    & dotnet restore ProjectLegion.slnf -p:Platform=x64 --disable-parallel -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw "Restore failed ($LASTEXITCODE)." }
    if (-not $Preview) {
        & dotnet build ProjectLegion.slnf -c Release -p:Platform=x64 -p:UseSharedCompilation=false --no-restore -m:1 -nr:false
        if ($LASTEXITCODE -ne 0) { throw "Managed solution build failed ($LASTEXITCODE)." }
    }
    $targetName = if ($Preview) { 'Preview' } else { 'Production' }
    $output = if ($Preview) { Join-Path $projectRoot 'BuildProjectLegion\Preview\bin' } else { Join-Path $projectRoot 'BuildProjectLegion\Production\Release' }
    $previewProperty = if ($Preview) { '-p:ProjectLegionPreviewBuild=true' } else { '-p:ProjectLegionPreviewBuild=false' }
    & dotnet build LenovoLegionToolkit.WPF\LenovoLegionToolkit.WPF.csproj -c Release -p:Platform=x64 -p:UseSharedCompilation=false $previewProperty --no-restore -m:1 -nr:false --output $output
    if ($LASTEXITCODE -ne 0) { throw "Application build failed ($LASTEXITCODE)." }
    Write-Output (Join-Path $output 'Project Legion.exe')
} finally {
    Pop-Location
}
