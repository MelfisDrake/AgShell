; AgShell installer: per user, no administrator rights, agshellctl on the user's PATH.
; Built by scripts/release.ps1, which passes AppVersion and SourceDir (the published app).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif

[Setup]
AppId={{C3A7E2D9-5B14-4F86-9E0A-7D2B6C1F8E35}
AppName=AgShell
AppVersion={#AppVersion}
AppVerName=AgShell {#AppVersion}
AppPublisher=MelfisDrake
AppPublisherURL=https://github.com/MelfisDrake/AgShell
DefaultDirName={localappdata}\Programs\AgShell
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=agshell-{#AppVersion}-setup
SetupIconFile=..\src\AgShell.App\Assets\agshell.ico
UninstallDisplayIcon={app}\agshell.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
; Closes the window; live daemons run from their copy in {localappdata}\agshell\live and keep their shells through an update.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.AgentGroup=Claude Code:
en.AgentGroup=Claude Code:
ru.InstallHooks=Установить хуки статуса агентов (Claude Code, Codex, Pi, OpenCode; копии настроек в .bak)
en.InstallHooks=Install the agent status hooks (Claude Code, Codex, Pi, OpenCode; settings backed up to .bak)
ru.InstallSkill=Установить скилл AgShell для Claude Code и Codex (skills/agshell в ~/.claude и ~/.codex)
en.InstallSkill=Install the AgShell skill for Claude Code and Codex (skills/agshell in ~/.claude and ~/.codex)
ru.EventLog=Зарегистрировать журнал событий AgShell (нужны права администратора)
en.EventLog=Register the AgShell event log (needs administrator rights)
ru.DesktopIcon=Ярлык на рабочем столе
en.DesktopIcon=Desktop shortcut
ru.OpenInAgShell=Открыть в AgShell
en.OpenInAgShell=Open in AgShell
ru.NoWebView=AgShell показывает окно через Microsoft Edge WebView2, а он не найден. Открыть страницу загрузки WebView2?
en.NoWebView=AgShell draws its window with Microsoft Edge WebView2, which was not found. Open the WebView2 download page?
ru.RestoreIncomplete=Не удалось полностью восстановить прежнюю установку. Резервная копия сохранена: %1
en.RestoreIncomplete=The previous installation could not be fully restored. Its backup was retained: %1
ru.BackupFailed=Не удалось сохранить резервную копию прежней установки. Установка остановлена до изменения её файлов. Файл или каталог: %1
en.BackupFailed=The previous installation could not be backed up. Setup stopped before changing its files. File or directory: %1

[Tasks]
Name: "hooks"; Description: "{cm:InstallHooks}"; GroupDescription: "{cm:AgentGroup}"
Name: "skill"; Description: "{cm:InstallSkill}"; GroupDescription: "{cm:AgentGroup}"
Name: "eventlog"; Description: "{cm:EventLog}"
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Themes and the page are replaced whole, so a theme dropped from the set does not linger.
Type: filesandordirs; Name: "{app}\web"
Type: filesandordirs; Name: "{app}\themes"

[UninstallDelete]
; The live daemon's copies; one a daemon still runs from stays.
Type: filesandordirs; Name: "{localappdata}\agshell\live"

[Registry]
; "Open in AgShell" on a folder, the inside of a folder and a drive, as agterm takes folders from Finder: a new session
; there. Windows 11 lists it under "Show more options"; the new menu takes only packaged apps' commands.
Root: HKCU; Subkey: "Software\Classes\Directory\shell\AgShell"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInAgShell}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\AgShell"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\agshell.exe"",0"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\AgShell\command"; ValueType: string; ValueName: ""; ValueData: """{app}\agshell.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\AgShell"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInAgShell}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\AgShell"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\agshell.exe"",0"
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\AgShell\command"; ValueType: string; ValueName: ""; ValueData: """{app}\agshell.exe"" ""%V"""
Root: HKCU; Subkey: "Software\Classes\Drive\shell\AgShell"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInAgShell}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Drive\shell\AgShell"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\agshell.exe"",0"
Root: HKCU; Subkey: "Software\Classes\Drive\shell\AgShell\command"; ValueType: string; ValueName: ""; ValueData: """{app}\agshell.exe"" ""%1"""

[Icons]
Name: "{userprograms}\AgShell"; Filename: "{app}\agshell.exe"
Name: "{userdesktop}\AgShell"; Filename: "{app}\agshell.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\agshellctl.exe"; Parameters: "install hooks"; Flags: runhidden; Tasks: hooks
Filename: "{app}\agshellctl.exe"; Parameters: "install skill"; Flags: runhidden; Tasks: skill
Filename: "{app}\agshell.exe"; Description: "{cm:LaunchProgram,AgShell}"; Flags: nowait postinstall skipifsilent

[Code]
const
  EnvironmentKey = 'Environment';
  EventLogPublisher = 'SOFTWARE\Microsoft\Windows\CurrentVersion\WINEVT\Publishers\{8d3f6c1a-52e4-4b7d-9a0e-6c2b1f47d935}';
  WebViewClient = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

#include "rollback.iss"

function WebViewInstalled: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, WebViewClient, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, WebViewClient, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

function InitializeSetup: Boolean;
var
  Code: Integer;
begin
  Result := True;
  if not WebViewInstalled then
  begin
    if MsgBox(CustomMessage('NoWebView'), mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOWNORMAL, ewNoWait, Code);
  end;
end;

{ The user's PATH gets the install folder once, so agshellctl works in any new terminal, and loses it on uninstall. }
function PathWithout(Path, Dir: String): String;
var
  Rest, Part: String;
  Index: Integer;
begin
  Result := '';
  Rest := Path;
  while Rest <> '' do
  begin
    Index := Pos(';', Rest);
    if Index = 0 then
    begin
      Part := Rest;
      Rest := '';
    end
    else
    begin
      Part := Copy(Rest, 1, Index - 1);
      Rest := Copy(Rest, Index + 1, Length(Rest));
    end;
    if (Part <> '') and (CompareText(RemoveBackslashUnlessRoot(Part), RemoveBackslashUnlessRoot(Dir)) <> 0) then
    begin
      if Result <> '' then
        Result := Result + ';';
      Result := Result + Part;
    end;
  end;
end;

function PowerShellLiteral(Value: String): String;
begin
  StringChangeEx(Value, '''', '''''', True);
  Result := '''' + Value + '''';
end;

function EventLogResourceAccessScript(Resources: String): String;
begin
  Result := 'try { $resource = [IO.FileInfo]::new(__RESOURCE__); ' +
    '$acl = $resource.GetAccessControl(); ' +
    '$acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(' +
    '[System.Security.Principal.SecurityIdentifier]::new(''S-1-5-80-880578595-1860270145-482643319-2788375705-1540778122''), ' +
    '[System.Security.AccessControl.FileSystemRights]::Read, [System.Security.AccessControl.AccessControlType]::Allow)); ' +
    '$resource.SetAccessControl($acl) } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }; ';
  StringChangeEx(Result, '__RESOURCE__', PowerShellLiteral(Resources), True);
end;

{ The event log provider (ADR 0011), as Help > Register Event Log... does: wevtutil through UAC, a dismissed prompt is skipped. }
procedure RegisterEventLog;
var
  Manifest, Resources, Script: String;
  ResultCode: Integer;
begin
  Manifest := ExpandConstant('{app}\AgShell.man');
  Resources := ExpandConstant('{app}\AgShell.EventLog.dll');
  Script := EventLogResourceAccessScript(Resources) +
    'wevtutil um ' + PowerShellLiteral(Manifest) + ' 2>$null; wevtutil im ' + PowerShellLiteral(Manifest) +
    ' ' + PowerShellLiteral('/rf:' + Resources) + ' ' + PowerShellLiteral('/mf:' + Resources) + '; exit $LASTEXITCODE';
  ShellExec('runas', ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -NonInteractive -Command "' + Script + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Unregisters the provider when this copy registered it; another copy's registration stays. }
procedure UnregisterEventLog;
var
  Resources: String;
  ResultCode: Integer;
begin
  if RegQueryStringValue(HKLM, EventLogPublisher, 'ResourceFileName', Resources)
    and (CompareText(Resources, ExpandConstant('{app}\AgShell.EventLog.dll')) = 0) then
    ShellExec('runas', ExpandConstant('{cmd}'), '/c wevtutil um "' + ExpandConstant('{app}\AgShell.man') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Path: String;
begin
  if CurStep = ssInstall then
    SavePreviousFiles;
  if CurStep = ssPostInstall then
  begin
    InstallationCommitted := True;
    if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Path) then
      Path := '';
    Path := PathWithout(Path, ExpandConstant('{app}'));
    if Path <> '' then
      Path := Path + ';';
    RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Path + ExpandConstant('{app}'));
    if WizardIsTaskSelected('eventlog') then
      RegisterEventLog;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Path: String;
begin
  if CurUninstallStep = usUninstall then
    UnregisterEventLog;
  if CurUninstallStep = usPostUninstall then
  begin
    if RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Path) then
      RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', PathWithout(Path, ExpandConstant('{app}')));
  end;
end;
