#define AppName "Windows Island"
#define AppPublisher "Cloudwhile"
#define AppUrl "https://github.com/Cloudwhile/WindowsIsland"
#ifndef PolicyRegistryPath
  #define PolicyRegistryPath "SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"
#endif

[Setup]
AppId={{8B397E71-1AA3-4F3B-911B-C08E0FAB484E}
AppName={#AppName}
AppPublisher={#AppPublisher}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
DefaultDirName={code:GetDefaultDirName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no
RestartApplications=no
OutputDir={#OutputDirectory}
OutputBaseFilename=WindowsIsland-{#AppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
Uninstallable=not IsPortable
CreateUninstallRegKey=not IsPortable
SetupIconFile={#PublishDirectory}\Assets\Island.ico
LicenseFile={#PublishDirectory}\LICENSE

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Excludes: "portable.marker"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackagingDirectory}\Uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackagingDirectory}\UninstallUser.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackagingDirectory}\InstallerPolicy.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackagingDirectory}\Unregister-Package.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackagingDirectory}\Write-InstallerResult.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackagingDirectory}\Convert-PortableDirectory.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackagingDirectory}\Close-InstalledApp.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackagingDirectory}\Close-InstalledApp.ps1"; Flags: dontcopy
Source: "{#PackagingDirectory}\Migrate-LegacyInstallation.ps1"; Flags: dontcopy

[Icons]
Name: "{commonprograms}\{#AppName}"; Filename: "{app}\WindowsIsland.exe"; IconFilename: "{app}\Assets\Island.ico"; Check: not IsPortable

[Run]
Filename: "{app}\WindowsIsland.exe"; Parameters: "--settings"; Description: "Launch {#AppName}"; Flags: postinstall nowait skipifsilent unchecked runasoriginaluser; Check: not IsUpdate

[UninstallRun]
; RemovePackageAsync stays in the interactive user's context, independent of installer elevation.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\Uninstall.ps1"" -InstallDirectory ""{app}"""; Flags: runhidden waituntilterminated; Check: not IsConversion
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\UninstallUser.ps1"" -InstallDirectory ""{app}"" -PreservePackage"; Flags: runhidden waituntilterminated; Check: IsConversion
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\InstallerPolicy.ps1"" -Action Restore -SnapshotPath ""{app}\installer-policy.json"" -RegistryPath ""{#PolicyRegistryPath}"""; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: files; Name: "{app}\AppxManifest.xml"; Check: not IsConversion
Type: files; Name: "{app}\resources.pri"; Check: not IsConversion
Type: files; Name: "{app}\installer-policy.json"
Type: dirifempty; Name: "{app}"

[Code]
var
  PortableMode: Boolean;
  InstalledSuccessfully: Boolean;
  PolicyPrepared: Boolean;
  ConversionScript: String;

function OpenEvent(Access: LongWord; Inherit: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function IsPortable(): Boolean;
begin
  Result := PortableMode;
end;

function IsUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function IsConversion(): Boolean;
begin
  Result := ExpandConstant('{param:CONVERT|0}') = '1';
end;

function GetDefaultDirName(Param: String): String;
begin
  if RegQueryStringValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B397E71-1AA3-4F3B-911B-C08E0FAB484E}_is1', 'InstallLocation', Result) then exit;
  if RegQueryStringValue(HKCU, 'Software\Cloudwhile\WindowsIsland\Installer', 'InstallDirectory', Result) then exit;
  if IsPortable() then Result := ExpandConstant('{src}\Windows Island')
  else Result := ExpandConstant('{autopf}\Windows Island');
end;

function InitializeSetup(): Boolean;
var
  Mode, Directory, Parameters: String;
  Index, ResultCode: Integer;
  EventHandle: THandle;
  EventName: String;
begin
  Directory := ExpandConstant('{param:DIR|}');
  Mode := ExpandConstant('{param:PORTABLE|auto}');
  PortableMode := (Mode = '1') or ((Mode = 'auto') and FileExists(AddBackslash(Directory) + 'portable.marker'));
  Result := True;
  if IsPortable() then begin
    if IsAdminInstallMode then begin
      SuppressibleMsgBox('Portable mode requires a non-elevated installation. Remove /ALLUSERS.', mbError, MB_OK, IDOK);
      Result := False;
    end;
  end else if not IsAdminInstallMode then begin
    { Inno handles the /ALLUSERS elevation and retains its original-user token. }
    Parameters := '/ALLUSERS';
    for Index := 1 to ParamCount do
      if (CompareText(ParamStr(Index), '/CURRENTUSER') <> 0) and (CompareText(ParamStr(Index), '/ALLUSERS') <> 0) then
        Parameters := Parameters + ' "' + ParamStr(Index) + '"';
    ShellExec('', ExpandConstant('{srcexe}'), Parameters, '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    Result := False;
  end;
  if Result and IsUpdate() then begin
    EventName := ExpandConstant('{param:UPDATEEVENT|}');
    if Pos('Local\WindowsIsland.Update.', EventName) = 1 then begin
      EventHandle := OpenEvent(2, False, EventName);
      if EventHandle <> 0 then begin
        SetEvent(EventHandle);
        CloseHandle(EventHandle);
      end;
    end;
  end;
end;

function PowerShell(Script, Parameters: String; OriginalUser: Boolean): Boolean;
var
  ResultCode: Integer;
  Arguments: String;
begin
  Arguments := '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + Script + '" ' + Parameters;
  if OriginalUser then
    Result := ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
    Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := Result and (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  ExtractTemporaryFile('Close-InstalledApp.ps1');
  ExtractTemporaryFile('Migrate-LegacyInstallation.ps1');
  if DirExists(ExpandConstant('{app}')) then begin
    if not PowerShell(ExpandConstant('{tmp}\Close-InstalledApp.ps1'), '-InstallDirectory "' + ExpandConstant('{app}') + '"', False) then begin
      Result := 'The previous Windows Island installation is still running.';
      exit;
    end;
    if not PowerShell(ExpandConstant('{tmp}\Migrate-LegacyInstallation.ps1'), '-InstallDirectory "' + ExpandConstant('{app}') + '" -Portable ' + IntToStr(Ord(IsPortable())) + ' -Scope CurrentUser', True) then begin
      Result := 'The previous user installation could not be migrated. Its data was preserved.';
      exit;
    end;
    if not PowerShell(ExpandConstant('{tmp}\Migrate-LegacyInstallation.ps1'), '-InstallDirectory "' + ExpandConstant('{app}') + '" -Portable ' + IntToStr(Ord(IsPortable())) + ' -Scope LocalMachine', False) then
      Result := 'The previous installation could not be migrated. Its data was preserved.';
  end;
end;

procedure CurStepChanged(CurrentStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurrentStep <> ssPostInstall then exit;
  if IsPortable() then begin
    if not SaveStringToFile(ExpandConstant('{app}\portable.marker'), '', False) then
      RaiseException('Could not preserve portable mode.');
  end else begin
    if not PowerShell(ExpandConstant('{app}\InstallerPolicy.ps1'), '-Action Prepare -SnapshotPath "' + ExpandConstant('{app}\installer-policy.json') + '" -RegistryPath "{#PolicyRegistryPath}"', False) then
      RaiseException('Could not prepare Windows package registration settings.');
    PolicyPrepared := True;
    if not Exec(ExpandConstant('{app}\WindowsIsland.exe'), '--prepare-installed-initialization', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      RaiseException('Could not prepare application registration resources.');
    if not ExecAsOriginalUser(ExpandConstant('{app}\WindowsIsland.exe'), '--install-initialization', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      Log('First-user package registration did not complete; the user can retry in Settings.');
    DeleteFile(ExpandConstant('{app}\portable.marker'));
  end;
  InstalledSuccessfully := True;
  if IsUpdate() then begin
    PowerShell(ExpandConstant('{app}\Write-InstallerResult.ps1'), '-InstallDirectory "' + ExpandConstant('{app}') + '" -Version "{#AppVersion}"', True);
    ExecAsOriginalUser(ExpandConstant('{app}\WindowsIsland.exe'), '--after-update --settings', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;

procedure DeinitializeSetup();
var
  ResultCode: Integer;
begin
  if PolicyPrepared and not InstalledSuccessfully then
    PowerShell(ExpandConstant('{app}\InstallerPolicy.ps1'), '-Action Restore -SnapshotPath "' + ExpandConstant('{app}\installer-policy.json') + '" -RegistryPath "{#PolicyRegistryPath}"', False);
  if IsUpdate() and not InstalledSuccessfully and FileExists(ExpandConstant('{app}\WindowsIsland.exe')) then begin
    PowerShell(ExpandConstant('{app}\Write-InstallerResult.ps1'), '-InstallDirectory "' + ExpandConstant('{app}') + '" -Version "{#AppVersion}" -Failed', True);
    ExecAsOriginalUser(ExpandConstant('{app}\WindowsIsland.exe'), '--after-update --settings', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if IsConversion() then begin
    ConversionScript := ExpandConstant('{tmp}\Convert-PortableDirectory.ps1');
    Result := FileCopy(ExpandConstant('{app}\Convert-PortableDirectory.ps1'), ConversionScript, False);
  end;
end;

procedure CurUninstallStepChanged(UserStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if (UserStep = usPostUninstall) and IsConversion() then begin
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + ConversionScript + '" -InstallDirectory "' + ExpandConstant('{app}') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      RaiseException('Could not prepare the former installation directory for portable use.');
  end;
end;
