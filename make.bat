@echo off
setlocal

set "VERSION=0.1.0.0"
if not "%~1"=="" set "VERSION=%~1"

dotnet restore LenovoLegionToolkit.WPF\LenovoLegionToolkit.WPF.csproj -p:Platform=x64 --disable-parallel -m:1 -nr:false || exit /b
dotnet publish LenovoLegionToolkit.WPF\LenovoLegionToolkit.WPF.csproj -c Release -p:Platform=x64 -p:Version=%VERSION% -p:FileVersion=%VERSION% --no-restore -o build || exit /b

where iscc >nul 2>nul || (echo Inno Setup 6 is required to build the installer.& exit /b 1)
iscc make_installer.iss /DMyAppVersion=%VERSION% || exit /b

echo Build output: build\Project Legion.exe
echo Installer output: build_installer\ProjectLegionSetup-v%VERSION%.exe
