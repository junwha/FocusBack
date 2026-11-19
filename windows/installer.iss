; Inno Setup script for the Windows installer (built by CI: iscc /DAppVersion=x.y.z windows\installer.iss).
; Per-user install, no admin rights. Your data in %APPDATA%\ADHD-PQ is never touched, also not on uninstall.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{19C03803-BF43-400E-85E0-ED4BAA83C0EF}
AppName=FocusQueue
AppVersion={#AppVersion}
AppPublisher=ADHDTimer
AppPublisherURL=https://github.com/junwha/ADHDTimer
DefaultDirName={localappdata}\Programs\FocusQueue
DefaultGroupName=FocusQueue
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=FocusQueue-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\PQ.exe
UninstallDisplayName=FocusQueue
CloseApplications=force

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start FocusQueue when Windows starts"

[Files]
Source: "PQ.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "pq_core.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\FocusQueue"; Filename: "{app}\PQ.exe"
Name: "{group}\FocusQueue stats"; Filename: "{app}\PQ.exe"; Parameters: "--stats"
Name: "{userstartup}\FocusQueue"; Filename: "{app}\PQ.exe"; Tasks: startup

[Run]
Filename: "{app}\PQ.exe"; Description: "Start FocusQueue now"; Flags: nowait postinstall skipifsilent

[Code]
// Before uninstalling, close only the PQ.exe that runs from this install folder (not a copy elsewhere).
function InitializeUninstall(): Boolean;
var Code: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -Command "Get-Process PQ -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq ''' +
    ExpandConstant('{app}') + '\PQ.exe'' } | ForEach-Object { $_.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800; if (-not $_.HasExited) { $_.Kill() } }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := True;
end;
