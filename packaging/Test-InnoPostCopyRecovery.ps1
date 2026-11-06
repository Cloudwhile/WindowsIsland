#Requires -Version 7.0
param(
    [string]$InnoSetup = '',
    [switch]$RequireRecovery
)
$ErrorActionPreference = 'Stop'
# Run only in the coordinated Windows guest. The payload is deliberately inert:
# this exercises the real installer without executing an application or registering a package.
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id = [Guid]::NewGuid().ToString('N')
$work = Join-Path $workspace "artifacts/verification/inno-post-copy/$id"
$payload = Join-Path $work 'payload'
$packaging = Join-Path $work 'packaging'
$installed = Join-Path $work 'installed'
$resultRoot = "WindowsIsland/Verification/$id/Updates"
$resultPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "$resultRoot/result.json"
if (!$InnoSetup) { $InnoSetup = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
if (!(Test-Path -LiteralPath $InnoSetup -PathType Leaf)) { $InnoSetup = (Get-Command iscc.exe -ErrorAction Stop).Source }
New-Item -ItemType Directory -Path $payload,$packaging,(Join-Path $payload 'Assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $workspace 'src/WindowsIsland/Assets/Island.ico') -Destination (Join-Path $payload 'Assets/Island.ico')
Copy-Item -LiteralPath (Join-Path $workspace 'LICENSE') -Destination $payload
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' | Copy-Item -Destination $packaging
$resultScript = Join-Path $packaging 'Write-InstallerResult.ps1'
[IO.File]::WriteAllText($resultScript, [IO.File]::ReadAllText($resultScript).Replace('WindowsIsland/Updates', $resultRoot))
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'WindowsIsland.iss'))
$source = $source.Replace('8B397E71-1AA3-4F3B-911B-C08E0FAB484E', [Guid]::NewGuid().ToString().ToUpperInvariant())
$source = $source.Replace('#define AppName "Windows Island"', '#define AppName "Island Recovery Probe ' + $id + '"')
# Portable mode avoids policy, package, shortcut and uninstall registration.
# Suppress only the update restart; the fixture EXE/DLL are not executable files.
$restart = 'ExecAsOriginalUser(ExpandConstant(''{app}\WindowsIsland.exe''), ''--after-update --settings'', '''', SW_SHOWNORMAL, ewNoWait, ResultCode);'
if (!$source.Contains($restart)) { throw 'Installer restart seam changed; review fixture isolation.' }
$source = $source.Replace($restart, 'Log(''Recovery fixture: application restart suppressed.'');')
# Observe that both files were actually replaced before the real marker write fails.
$postCopy = "  if CurrentStep <> ssPostInstall then exit;"
if (!$source.Contains($postCopy)) { throw 'Installer post-copy seam changed; review fixture.' }
$source = $source.Replace($postCopy, $postCopy + @'

  if ExpandConstant('{param:PROBEFAIL|0}') = '1' then begin
    FileCopy(ExpandConstant('{app}\WindowsIsland.exe'), ExpandConstant('{app}\observed-post-copy.exe'), False);
    FileCopy(ExpandConstant('{app}\WindowsIsland.dll'), ExpandConstant('{app}\observed-post-copy.dll'), False);
  end;
'@)
$fixture = Join-Path $packaging 'WindowsIsland.iss'
[IO.File]::WriteAllText($fixture, $source)
function Build-Probe([string]$version, [string]$content) {
    foreach ($name in @('WindowsIsland.exe','WindowsIsland.dll')) {
        [IO.File]::WriteAllText((Join-Path $payload $name), "$content/$name")
    }
    & $InnoSetup '/Q' "/DAppVersion=$version" "/DPublishDirectory=$payload" "/DPackagingDirectory=$packaging" "/DOutputDirectory=$work" $fixture | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Probe compilation failed: $version" }
    return (Join-Path $work "WindowsIsland-$version-win-x64.exe")
}
function Run-Probe([string]$installer, [string]$log, [switch]$Fail) {
    $arguments = @('/CURRENTUSER','/PORTABLE=1','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $installed + '"'),('/LOG="' + (Join-Path $work $log) + '"'))
    if ($Fail) { $arguments += '/UPDATE=1','/PROBEFAIL=1' }
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if (!$process.WaitForExit(120000)) { throw "Probe installer timed out; evidence retained at $work" }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
$oldInstaller = Build-Probe '0.1.0' 'previous-payload'
$newInstaller = Build-Probe '0.1.1' 'replacement-payload'
if ((Run-Probe $oldInstaller 'baseline.log') -ne 0) { throw 'Baseline probe install failed.' }
$oldHashes = @{}
foreach ($name in @('WindowsIsland.exe','WindowsIsland.dll')) { $oldHashes[$name] = (Get-FileHash -LiteralPath (Join-Path $installed $name)).Hash }
New-Item -ItemType Directory -Path (Join-Path $installed 'Plugins/data/canary') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $installed 'settings.json'), 'preserved-settings')
[IO.File]::WriteAllText((Join-Path $installed 'Plugins/data/canary/state.txt'), 'preserved-plugin-data')
# A directory with the marker's name deterministically makes SaveStringToFile fail.
Remove-Item -LiteralPath (Join-Path $installed 'portable.marker')
New-Item -ItemType Directory -Path (Join-Path $installed 'portable.marker') | Out-Null
$exitCode = Run-Probe $newInstaller 'post-copy-failure.log' -Fail
$restored = $true
foreach ($name in @('WindowsIsland.exe','WindowsIsland.dll')) {
    $observed = Join-Path $installed ('observed-post-copy.' + [IO.Path]::GetExtension($name).TrimStart('.'))
    if (!(Test-Path -LiteralPath $observed) -or [IO.File]::ReadAllText($observed) -cne "replacement-payload/$name") { throw "Post-copy failure was not exercised for $name" }
    $restored = $restored -and (Test-Path -LiteralPath (Join-Path $installed $name)) -and ((Get-FileHash -LiteralPath (Join-Path $installed $name)).Hash -ceq $oldHashes[$name])
}
$dataPreserved = [IO.File]::ReadAllText((Join-Path $installed 'settings.json')) -ceq 'preserved-settings' -and
    [IO.File]::ReadAllText((Join-Path $installed 'Plugins/data/canary/state.txt')) -ceq 'preserved-plugin-data'
$result = if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json } else { $null }
$failureReported = $null -ne $result -and $result.Success -eq $false -and $result.MessageKey -ceq 'UpdateFailed' -and $result.InstallDirectory -ieq $installed
$report = [ordered]@{
    WorkDirectory = $work; InstallerExitCode = $exitCode; PostCopyReplacementObserved = $true
    PreviousExeAndDllRestored = $restored; DataPreserved = $dataPreserved
    FailureResultPublished = $failureReported; ResultPath = $resultPath
    RecoveryPassed = ($exitCode -ne 0 -and $restored -and $dataPreserved -and $failureReported)
}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'report.json') -Encoding utf8
$report | ConvertTo-Json
if (!$dataPreserved) { throw 'Installer modified isolated user data.' }
if ($RequireRecovery -and !$report.RecoveryPassed) { throw "Post-copy recovery regression failed; evidence retained at $work" }
