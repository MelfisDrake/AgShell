#define FixtureSource AddBackslash(SourcePath) + "payload"
[Setup]
AppId=AgShellInstallerRollbackFixture
AppName=AgShell Installer Rollback Fixture
AppVersion=0.0.0
DefaultDirName={tmp}\unused
PrivilegesRequired=lowest
Uninstallable=no
CreateUninstallRegKey=no
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
CloseApplications=no
OutputBaseFilename=rollback-fixture

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
en.RestoreIncomplete=The previous installation could not be fully restored. Its backup was retained: %1
en.BackupFailed=The previous installation could not be backed up. Setup stopped before changing its files. File or directory: %1

[InstallDelete]
Type: filesandordirs; Name: "{app}\web"
Type: filesandordirs; Name: "{app}\themes"

[Files]
Source: "{#FixtureSource}\early.txt"; DestDir: "{app}"; AfterInstall: CancelAfterEarly
Source: "{#FixtureSource}\locked.txt"; DestDir: "{app}"
Source: "{#FixtureSource}\web\*"; DestDir: "{app}\web"
Source: "{#FixtureSource}\themes\*"; DestDir: "{app}\themes"

[Code]
#include "..\..\installer\rollback.iss"

procedure CancelAfterEarly;
begin
  if ExpandConstant('{param:CancelAfterEarly|0}') = '1' then
    WizardForm.Close;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  Confirm := False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    SavePreviousFiles;
  if CurStep = ssPostInstall then
    InstallationCommitted := True;
end;
