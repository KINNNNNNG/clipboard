#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\windows\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\windows\installer"
#endif

[Setup]
AppId={{B2A2AFB4-8B91-4E92-BB2D-5B6D3B7EA8D8}
AppName=Clipboard
AppVersion={#AppVersion}
AppPublisher=Clipboard
DefaultDirName={localappdata}\Programs\Clipboard
DefaultGroupName=Clipboard
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Clipboard-Setup-v{#AppVersion}
SetupIconFile={#SourceDir}\Assets\Clipboard.ico
UninstallDisplayIcon={app}\Clipboard.Windows.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
Uninstallable=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Clipboard"; Filename: "{app}\Clipboard.Windows.exe"; WorkingDir: "{app}"
Name: "{commondesktop}\Clipboard"; Filename: "{app}\Clipboard.Windows.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Clipboard.Windows.exe"; Description: "启动 Clipboard"; Flags: nowait postinstall skipifsilent
