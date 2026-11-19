; Inno Setup script for the Windows installer (built by CI: iscc /DAppVersion=x.y.z windows\installer.iss).
; Per-user install, no admin rights. Your data in %APPDATA%\FocusBack is never touched, also not on uninstall.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{19C03803-BF43-400E-85E0-ED4BAA83C0EF}
AppName=FocusBack
AppVersion={#AppVersion}
AppPublisher=FocusBack
AppPublisherURL=https://github.com/junwha/FocusBack
DefaultDirName={localappdata}\Programs\FocusBack
DefaultGroupName=FocusBack
DisableProgramGroupPage=yes
UsePreviousAppDir=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=FocusBack-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\FocusBack.exe
UninstallDisplayName=FocusBack
CloseApplications=force

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start FocusBack when Windows starts"

[Files]
Source: "FocusBack.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "pq_core.dll"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; clean up installs from before the rename (FocusQueue 0.1.x)
Type: filesandordirs; Name: "{localappdata}\Programs\FocusQueue"
Type: filesandordirs; Name: "{userprograms}\FocusQueue"
Type: files; Name: "{userstartup}\FocusQueue.lnk"

[Icons]
Name: "{group}\FocusBack"; Filename: "{app}\FocusBack.exe"
Name: "{group}\FocusBack stats"; Filename: "{app}\FocusBack.exe"; Parameters: "--stats"
Name: "{userstartup}\FocusBack"; Filename: "{app}\FocusBack.exe"; Tasks: startup

[Run]
Filename: "{app}\FocusBack.exe"; Description: "Start FocusBack now"; Flags: nowait postinstall skipifsilent

[Code]
// Before uninstalling, close only the FocusBack.exe that runs from this install folder (not a copy elsewhere).
function InitializeUninstall(): Boolean;
var Code: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -Command "Get-Process FocusBack -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq ''' +
    ExpandConstant('{app}') + '\FocusBack.exe'' } | ForEach-Object { $_.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800; if (-not $_.HasExited) { $_.Kill() } }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := True;
end;
