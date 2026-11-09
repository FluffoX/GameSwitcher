; Inno Setup script for GameSwitcher. Built by Release.ps1:
;   ISCC.exe /DAppVersion=2.2.0 /DSourceDir=<staged files> /DOutputDir=<dist> installer\GameSwitcher.iss
; Installs per user (no admin rights) to %LocalAppData%\Programs\GameSwitcher. Settings stay in
; %AppData%\GameSwitcher, so updates and uninstalling never touch them.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\stage"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{8F3C2A51-6B7D-4E2A-9C1F-3D5E7A9B1C24}
AppName=GameSwitcher
AppVersion={#AppVersion}
AppVerName=GameSwitcher {#AppVersion}
AppPublisher=FluffoX
AppPublisherURL=https://github.com/FluffoX/GameSwitcher
AppSupportURL=https://github.com/FluffoX/GameSwitcher/issues
AppUpdatesURL=https://github.com/FluffoX/GameSwitcher/releases
AppCopyright=Copyright (c) FluffoX - MIT License
VersionInfoVersion={#AppVersion}
VersionInfoProductName=GameSwitcher
DefaultDirName={localappdata}\Programs\GameSwitcher
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=GameSwitcher-Setup-{#AppVersion}
SetupIconFile={#SourceDir}\app\icon.ico
UninstallDisplayIcon={app}\GameSwitcher.exe
UninstallDisplayName=GameSwitcher
LicenseFile={#SourceDir}\LICENSE
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "pt"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"

[InstallDelete]
; drop files of the previous version, so removed scripts don't linger after an update
Type: filesandordirs; Name: "{app}\app"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{userprograms}\GameSwitcher"; Filename: "{app}\GameSwitcher.exe"; WorkingDir: "{app}"

[Run]
; after an update, restart the background service if autostart is set up (fails silently otherwise)
Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN GameSwitcher"; Flags: runhidden
Filename: "{app}\GameSwitcher.exe"; Description: "{cm:LaunchProgram,GameSwitcher}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM GameSwitcher.exe"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\app\Install.ps1"" -Uninstall"; Flags: runhidden; RunOnceId: "RemoveAutostart"

[Code]
// Close a running GameSwitcher (GUI and background service) before files are replaced
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GameSwitcher.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;
