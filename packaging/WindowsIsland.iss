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
DefaultDirName={localappdata}\Programs\Windows Island
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
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

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\WindowsIsland.exe"; IconFilename: "{app}\Assets\Island.ico"

[Run]
Filename: "{app}\WindowsIsland.exe"; Parameters: "--settings"; Description: "Launch {#AppName}"; Flags: postinstall nowait skipifsilent unchecked runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\Uninstall.ps1"" -InstallDirectory ""{app}"""; RunOnceId: "WindowsIsland.Unregister"

[Code]
function InitializePackage(): Boolean;
var
  ResultCode: Integer;
begin
  Result := ExecAsOriginalUser(ExpandConstant('{app}\WindowsIsland.exe'), '--install-initialization', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    MsgBox('Windows Island package initialization failed. Please retry the installation or run it as administrator.', mbError, MB_OK);
end;

procedure CurStepChanged(CurrentStep: TSetupStep);
begin
  if CurrentStep = ssPostInstall then
    if not InitializePackage() then
      Abort;
end;
