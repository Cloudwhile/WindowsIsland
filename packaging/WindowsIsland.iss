#define AppName "Windows Island"
#define AppPublisher "Cloudwhile"
#define AppUrl "https://github.com/Cloudwhile/WindowsIsland"

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
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The upgrade hook below closes only the WindowsIsland process from {app}.
; Restart Manager would also close Telegram while it has our hook DLL loaded.
CloseApplications=no
RestartApplications=no
OutputDir={#OutputDirectory}
OutputBaseFilename=WindowsIsland-{#AppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
Uninstallable=yes
SetupIconFile={#PublishDirectory}\Assets\Island.ico
LicenseFile={#PublishDirectory}\LICENSE

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackagingDirectory}\Uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackagingDirectory}\UninstallUser.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\WindowsIsland.exe"; IconFilename: "{app}\Assets\Island.ico"

[Run]
Filename: "{app}\WindowsIsland.exe"; Parameters: "--settings"; Description: "Launch {#AppName}"; Flags: postinstall nowait skipifsilent unchecked runasoriginaluser

[UninstallDelete]
Type: files; Name: "{app}\AppxManifest.xml"
Type: files; Name: "{app}\resources.pri"
Type: dirifempty; Name: "{app}"

[Code]
function GetDefaultDirName(Param: String): String;
begin
  if RegQueryStringValue(HKCU, 'Software\Cloudwhile\WindowsIsland\Installer', 'InstallDirectory', Result) then
    exit;
  Result := ExpandConstant('{localappdata}\Programs\Windows Island');
end;

function RunUpgradeCleanup(): Boolean;
var
  ResultCode: Integer;
  Parameters: String;
begin
  Result := True;
  if not FileExists(ExpandConstant('{app}\WindowsIsland.exe')) or
     not FileExists(ExpandConstant('{app}\Uninstall.ps1')) then
    exit;
  Parameters := '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File ' +
    '"' + ExpandConstant('{app}\Uninstall.ps1') + '" -InstallDirectory "' +
    ExpandConstant('{app}') + '" -PreserveManagedHook';
  Result := ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    SuppressibleMsgBox('Windows Island could not close the previous installation before updating.', mbError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not RunUpgradeCleanup() then
    Result := 'The previous Windows Island installation is still running.';
end;

procedure CurUninstallStepChanged(UserStep: TUninstallStep);
var
  ResultCode: Integer;
  Parameters: String;
begin
  if UserStep <> usUninstall then
    exit;
  if not FileExists(ExpandConstant('{app}\Uninstall.ps1')) then
    exit;
  Parameters := '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File ' +
    '"' + ExpandConstant('{app}\Uninstall.ps1') + '" -InstallDirectory "' +
    ExpandConstant('{app}') + '"';
  { ExecAsOriginalUser is only available during Setup. The cleanup script
    dispatches per-user work to the interactive user's token when elevated. }
  if not (Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
    SuppressibleMsgBox('Windows Island cleanup did not complete. Some registration may remain.', mbError, MB_OK, IDOK);
end;

function InitializePackage(): Boolean;
var
  ResultCode: Integer;
begin
  Result := ExecAsOriginalUser(ExpandConstant('{app}\WindowsIsland.exe'), '--install-initialization', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    SuppressibleMsgBox('Windows Island package initialization failed. Please retry the installation or run it as administrator.', mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurrentStep: TSetupStep);
begin
  if CurrentStep = ssPostInstall then
    if not InitializePackage() then
      Abort;
end;
