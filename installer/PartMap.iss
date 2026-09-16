#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{C68CC171-E99B-43F4-8843-17BD1E234205}
AppName=PartMap
AppVersion={#AppVersion}
AppPublisher=hellysu
DefaultDirName={localappdata}\Programs\PartMap
DefaultGroupName=PartMap
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=PartMap-Setup-win-x64-v{#AppVersion}
SetupIconFile=..\PartMap-icon.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\PartMap.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项："; Flags: unchecked

[Files]
Source: "..\dist\PartMap-win10-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{app}\products"; Flags: uninsneveruninstall

[Icons]
Name: "{autoprograms}\PartMap"; Filename: "{app}\PartMap.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\PartMap"; Filename: "{app}\PartMap.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\PartMap.exe"; Description: "启动 PartMap"; Flags: nowait postinstall skipifsilent
