$ErrorActionPreference = 'Stop'
# Canonical paths and palette live in assets/branding/geometry.json.
# Python writes SVG/native drawing resources; Windows WPF renders matching PNG/ICO assets.
& python (Join-Path $PSScriptRoot 'generate_brand_assets.py')
if ($LASTEXITCODE -ne 0) { throw 'Brand asset generation failed.' }
