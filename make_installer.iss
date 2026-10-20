#include "InnoDependencies\Scripts\install_dotnet.iss"

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0.0"
#endif

[Setup]
AppId={{58A2A3AB-3D3F-4A02-B32B-303ABF19D22D}
AppMutex=ProjectLegion_Mutex_70ade129-b5d8-4d78-8c5f-63dbbfbb1942
AppName=Project Legion
AppVersion={#MyAppVersion}
AppPublisher=Project Legion contributors
DefaultDirName={autopf}\ProjectLegion
DisableProgramGroupPage=yes
LicenseFile=LICENSE
PrivilegesRequired=admin
OutputBaseFilename=ProjectLegionSetup-v{#MyAppVersion}
OutputDir=build_installer
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Project Legion
UninstallDisplayIcon={app}\Project Legion.exe
SetupIconFile=InnoDependencies\Images\setup_icon.ico
WizardSmallImageFile=InnoDependencies\Images\logo.png
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "build\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "NOTICE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Project Legion"; Filename: "{app}\Project Legion.exe"; AppUserModelID: "ProjectLegion.Desktop"
Name: "{autodesktop}\Project Legion"; Filename: "{app}\Project Legion.exe"; Tasks: desktopicon; AppUserModelID: "ProjectLegion.Desktop"

[Run]
Filename: "rundll32.exe"; Parameters: "shell32.dll,ShellExec_RunDLL ""{app}\Project Legion.exe"""; Description: "Launch Project Legion"; Flags: runascurrentuser nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
begin
  InstallDotNetDesktopRuntime;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    Exec('schtasks.exe', '/Delete /TN "ProjectLegion_Autorun_70ade129-b5d8-4d78-8c5f-63dbbfbb1942" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
