var
  PreviousFiles: String;
  PreviousFilesReady, InstallationCommitted: Boolean;

function FileAttributes(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function EnumerationError: LongWord;
  external 'GetLastError@kernel32.dll stdcall';

procedure BackupError(Reason, Path: String);
begin
  Log(Reason + ': ' + Path);
  RaiseException(FmtMessage(CustomMessage('BackupFailed'), [Path]));
end;

procedure CopyPreviousFiles(Source, Destination: String);
var
  Entry: TFindRec;
  FromPath, ToPath: String;
begin
  if (FileAttributes(Source) and $400) <> 0 then
    BackupError('Cannot back up a reparse point', Source);
  if not ForceDirectories(Destination) then
    BackupError('Cannot create installation backup', Destination);
  if not FindFirst(Source + '\*', Entry) then
    BackupError('Cannot enumerate installation directory', Source);
  try
    repeat
      if (Entry.Name <> '.') and (Entry.Name <> '..') then
      begin
        FromPath := Source + '\' + Entry.Name;
        ToPath := Destination + '\' + Entry.Name;
        if (Entry.Attributes and $400) <> 0 then
          BackupError('Cannot back up a reparse point', FromPath);
        if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          CopyPreviousFiles(FromPath, ToPath)
        else if not FileCopy(FromPath, ToPath, True) then
          BackupError('Cannot back up installation file', FromPath)
        else if GetSHA256OfFile(FromPath) <> GetSHA256OfFile(ToPath) then
          BackupError('Installation file changed during backup', FromPath);
      end;
    until not FindNext(Entry);
    if EnumerationError <> 18 then
      BackupError('Cannot finish enumerating installation directory', Source);
  finally
    FindClose(Entry);
  end;
end;

procedure SavePreviousFiles;
var
  Attributes, Error: LongWord;
begin
  Attributes := FileAttributes(ExpandConstant('{app}'));
  if Attributes = $FFFFFFFF then
  begin
    Error := EnumerationError;
    if (Error = 2) or (Error = 3) then
      exit;
    BackupError('Cannot inspect installation directory', ExpandConstant('{app}'));
  end;
  if (Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
    BackupError('Installation path is not a directory', ExpandConstant('{app}'));
  { A sibling survives Setup's automatic removal of its own temporary directory. }
  PreviousFiles := RemoveBackslashUnlessRoot(ExpandConstant('{tmp}')) + '-previous';
  if FileExists(PreviousFiles) or DirExists(PreviousFiles) then
  begin
    PreviousFiles := '';
    BackupError('Installation backup path already exists', ExpandConstant('{tmp}') + '-previous');
  end;
  Log('Saving previous installation files to ' + PreviousFiles);
  CopyPreviousFiles(ExpandConstant('{app}'), PreviousFiles);
  PreviousFilesReady := True;
end;

function RestorePreviousFiles(Source, Destination: String): Boolean;
var
  Entry: TFindRec;
  FromPath, ToPath: String;
  Same: Boolean;
begin
  Result := ForceDirectories(Destination);
  if not Result then
    exit;
  if not FindFirst(Source + '\*', Entry) then
  begin
    Result := False;
    exit;
  end;
  try
    repeat
      if (Entry.Name <> '.') and (Entry.Name <> '..') then
      begin
        FromPath := Source + '\' + Entry.Name;
        ToPath := Destination + '\' + Entry.Name;
        if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          if not RestorePreviousFiles(FromPath, ToPath) then
            Result := False;
        end
        else
        begin
          try
            { A running executable can stay locked when its original bytes are already in place. }
            Same := FileExists(ToPath) and
              (GetSHA256OfFile(FromPath) = GetSHA256OfFile(ToPath));
            if not Same then
              if not FileCopy(FromPath, ToPath, False) then
                RaiseException('Cannot restore installation file: ' + ToPath);
            if GetSHA256OfFile(FromPath) <> GetSHA256OfFile(ToPath) then
              RaiseException('Restored installation file differs: ' + ToPath);
          except
            Log(GetExceptionMessage);
            Result := False;
          end;
        end;
      end;
    until not FindNext(Entry);
    if EnumerationError <> 18 then
      Result := False;
  finally
    FindClose(Entry);
  end;
end;

procedure DeinitializeSetup;
var
  Restored: Boolean;
begin
  Restored := True;
  if PreviousFilesReady and not InstallationCommitted then
  begin
    Log('Restoring previous installation files after Setup rollback.');
    Restored := RestorePreviousFiles(PreviousFiles, ExpandConstant('{app}'));
    if Restored then
      Log('Previous installation files restored and SHA256 verified.')
    else
    begin
      Log('Previous installation restore incomplete; backup retained at ' + PreviousFiles);
      SuppressibleMsgBox(FmtMessage(CustomMessage('RestoreIncomplete'), [PreviousFiles]), mbError, MB_OK, IDOK);
    end;
  end;
  if Restored and (PreviousFiles <> '') then
    if not DelTree(PreviousFiles, True, True, True) then
      Log('Could not remove installation backup: ' + PreviousFiles);
end;
