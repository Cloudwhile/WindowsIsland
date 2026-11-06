#Requires -Version 7.0
param([string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'), [switch]$Machine, [switch]$ExerciseTransitions)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id = [Guid]::NewGuid().ToString('N')
$work = Join-Path $workspace "artifacts/verification/inno/$id"
$payload = Join-Path $work 'payload'
$packaging = Join-Path $work 'packaging'
$installed = Join-Path $work 'installed'
$packageName = 'Island.Inno.' + $id.Substring(0, 20)
$appName = 'Island Inno Test ' + $id
$appId = [Guid]::NewGuid().ToString().ToUpperInvariant()
$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = (Get-Command iscc.exe -ErrorAction Stop).Source }
New-Item -ItemType Directory -Path $payload,$packaging -Force | Out-Null
$originalProcesses = @(Get-Process WindowsIsland -ErrorAction SilentlyContinue | Select-Object Id,StartTime,Path)
$originalStartup = (Get-ItemProperty 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Run' -ErrorAction SilentlyContinue).WindowsIsland
function Appx([string]$command) {
    $output = & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -NonInteractive -Command $command
    if ($LASTEXITCODE -ne 0) { throw 'Package inspection failed.' }
    return ($output -join "`n")
}
$originalRegistration = Appx 'Get-AppxPackage -Name WindowsIsland.Desktop | Select-Object PackageFullName,InstallLocation | ConvertTo-Json -Compress'
foreach ($item in Get-ChildItem -LiteralPath (Resolve-Path $PublishDirectory).Path -Force) {
    if ($item.Name -notlike 'verification-*' -and $item.Name -ne 'Updater') { Copy-Item -LiteralPath $item.FullName -Destination $payload -Recurse }
}
Copy-Item -LiteralPath (Join-Path $workspace 'LICENSE') -Destination $payload
$manifest = Join-Path $payload 'Initialization/AppxManifest.xml'
([IO.File]::ReadAllText($manifest)).Replace('WindowsIsland.Desktop', $packageName).Replace('CN=WindowsIsland', 'CN=IslandInnoTest') |
    Set-Content -LiteralPath $manifest -Encoding UTF8
foreach ($file in @('Uninstall.ps1','UninstallUser.ps1','Unregister-Package.ps1','InstallerPolicy.ps1','Close-InstalledApp.ps1','Convert-InnoInstallation.ps1','Write-InstallerResult.ps1','Convert-PortableDirectory.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $packaging
}
$conversion = Join-Path $packaging 'Convert-InnoInstallation.ps1'
[IO.File]::WriteAllText($conversion, [IO.File]::ReadAllText($conversion).Replace('8B397E71-1AA3-4F3B-911B-C08E0FAB484E', $appId))
# The conversion check owns a separate startup value, never the real preference.
$userCleanup = Join-Path $packaging 'UninstallUser.ps1'
[IO.File]::WriteAllText($userCleanup, [IO.File]::ReadAllText($userCleanup).Replace("'WindowsIsland'", "'$packageName'"))
$resultScript = Join-Path $packaging 'Write-InstallerResult.ps1'
[IO.File]::WriteAllText($resultScript, [IO.File]::ReadAllText($resultScript).Replace('WindowsIsland/Updates', "WindowsIsland/Verification/$id/Updates"))
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'WindowsIsland.iss'))
$source = $source.Replace('#define AppName "Windows Island"', '#define AppName "' + $appName + '"')
$source = $source.Replace('8B397E71-1AA3-4F3B-911B-C08E0FAB484E', $appId)
# Machine checks use an isolated policy subtree; product policy values are never changed.
$source = $source.Replace('SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock', 'SOFTWARE\Cloudwhile\InstallerChecks\' + $id)
$source = $source.Replace('--after-update --settings', '--verify-local --settings')
if ($Machine) {
    $source = $source.Replace('if (UserStep = usPostUninstall) and IsConversion() then begin',
        "if (UserStep = usPostUninstall) and not IsConversion() then RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\Cloudwhile\InstallerChecks\$id');" + "`n" + '  if (UserStep = usPostUninstall) and IsConversion() then begin')
}
[IO.File]::WriteAllText((Join-Path $packaging 'WindowsIsland.iss'), $source)
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'WindowsIsland.exe')).ProductVersion.Split('+')[0].TrimStart('v')
$installer = Join-Path $work "WindowsIsland-$version-win-x64.exe"
function Run-Installer([string]$executable, [string[]]$arguments) {
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if (!$process.WaitForExit(120000)) { throw "Installer timed out: $executable" }
        if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode)" }
    } finally { $process.Dispose() }
}
function Assert-Data {
    if ([IO.File]::ReadAllText((Join-Path $installed 'settings.json')) -cne 'preserved user settings' -or
        [IO.File]::ReadAllText((Join-Path $installed 'Plugins/data/check/state.txt')) -cne 'preserved plugin data') { throw 'Installation did not preserve data.' }
}
function Exercise-Update([string[]]$distribution) {
    $executable = Join-Path $installed 'WindowsIsland.exe'
    $previous = Start-Process -FilePath $executable -ArgumentList '--verify-local','--settings' -WindowStyle Hidden -PassThru
    try {
        Start-Sleep -Milliseconds 1200
        if ($previous.HasExited) { throw 'The isolated application did not start.' }
        $eventName = 'Local\WindowsIsland.Update.' + [Guid]::NewGuid().ToString('N')
        $ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $eventName)
        try {
            $process = Start-Process -FilePath $installer -ArgumentList ($distribution + @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/UPDATE=1',('/UPDATEEVENT=' + $eventName),('/DIR="' + $installed + '"'),('/LOG="' + (Join-Path $work ('update-' + $distribution[0].TrimStart('/') + '.log')) + '"'))) -WindowStyle Hidden -PassThru
            try {
                if (!$ready.WaitOne(60000)) { throw 'Installer never acknowledged its final privilege mode.' }
                if (!$process.WaitForExit(120000) -or $process.ExitCode -ne 0) { throw 'Silent update failed.' }
            } finally { $process.Dispose() }
        } finally { $ready.Dispose() }
        if (!$previous.WaitForExit(10000)) { throw 'Installer did not close its previous instance.' }
        $restarted = @()
        for ($attempt = 0; $attempt -lt 100; $attempt++) {
            $restarted = @(Get-Process WindowsIsland -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $executable -and $_.Id -ne $previous.Id })
            if ($restarted.Count) { break }
            Start-Sleep -Milliseconds 100
        }
        if ($restarted.Count -ne 1) { throw 'Update did not restart exactly one application in the same directory.' }
        $result = Get-Content -LiteralPath (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "WindowsIsland/Verification/$id/Updates/result.json") -Raw | ConvertFrom-Json
        if (!$result.Success -or $result.InstallDirectory -ine $installed -or $result.Version -cne $version) { throw 'Update did not verify both installed product versions.' }
        Assert-Data
        'PASS silent update acknowledges launch, closes the old instance, verifies versions and restarts one instance: ' + $distribution[0]
    } finally {
        & (Join-Path $installed 'Close-InstalledApp.ps1') -InstallDirectory $installed
        $previous.Dispose()
    }
}
$mode = if ($Machine) { @('/ALLUSERS','/PORTABLE=0') } else { @('/CURRENTUSER','/PORTABLE=1') }
try {
    & $compiler '/Q' "/DAppVersion=$version" "/DPublishDirectory=$payload" "/DPackagingDirectory=$packaging" "/DOutputDirectory=$work" (Join-Path $packaging 'WindowsIsland.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated Inno build failed.' }
    Run-Installer $installer ($mode + @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $installed + '"'),('/LOG="' + (Join-Path $work 'install.log') + '"')))
    foreach ($file in @('WindowsIsland.exe','Unregister-Package.ps1','en-US/WindowsIsland.resources.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $installed $file))) { throw "Missing installed file: $file" }
    }
    if (!$Machine) {
        if (!(Test-Path -LiteralPath (Join-Path $installed 'portable.marker')) -or (Test-Path -LiteralPath (Join-Path $installed 'unins000.exe'))) { throw 'Portable installation created uninstall metadata or lost its marker.' }
        if (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Programs')) ($appName + '.lnk'))) { throw 'Portable installation created a shortcut.' }
    } else {
        if ((Appx "(Get-AppxPackage -Name '$packageName').InstallLocation").TrimEnd('\') -ine $installed) { throw 'First-user package registration failed.' }
        if (!(Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) ($appName + '.lnk')))) { throw 'Machine start menu shortcut missing.' }
    }
    'PASS isolated installation retains its requested distribution mode'
    [IO.File]::WriteAllText((Join-Path $installed 'settings.json'), 'preserved user settings')
    New-Item -ItemType Directory -Path (Join-Path $installed 'Plugins/data/check') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $installed 'Plugins/data/check/state.txt'), 'preserved plugin data')
    Run-Installer $installer ($mode + @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $installed + '"'),('/LOG="' + (Join-Path $work 'upgrade.log') + '"')))
    if ([IO.File]::ReadAllText((Join-Path $installed 'settings.json')) -cne 'preserved user settings' -or
        [IO.File]::ReadAllText((Join-Path $installed 'Plugins/data/check/state.txt')) -cne 'preserved plugin data') { throw 'Upgrade did not preserve data.' }
    'PASS isolated in-place upgrade preserves settings and plugin data'
    if ($ExerciseTransitions) {
        Exercise-Update $mode
        if ($Machine) {
            $startupPath = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Run'
            New-ItemProperty -Path $startupPath -Name $packageName -Value ('"' + (Join-Path $installed 'WindowsIsland.exe') + '" --startup') -PropertyType String -Force | Out-Null
            Run-Installer $installer @('/CURRENTUSER','/PORTABLE=1','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $installed + '"'),('/LOG="' + (Join-Path $work 'convert-portable.log') + '"'))
            Assert-Data
            if (!(Test-Path (Join-Path $installed 'portable.marker')) -or (Test-Path (Join-Path $installed 'unins000.exe')) -or
                (Test-Path "HKLM:/Software/Microsoft/Windows/CurrentVersion/Uninstall/{$appId}_is1") -or
                (Get-ItemProperty -Path $startupPath -Name $packageName -ErrorAction SilentlyContinue)) { throw 'Conversion left machine registration, startup or uninstall metadata.' }
            'PASS machine to portable conversion preserves data and removes machine registration and startup'
            Exercise-Update @('/CURRENTUSER','/PORTABLE=1')
            Run-Installer $installer @('/ALLUSERS','/PORTABLE=0','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $installed + '"'),('/LOG="' + (Join-Path $work 'convert-machine.log') + '"'))
            Assert-Data
            if ((Test-Path (Join-Path $installed 'portable.marker')) -or !(Test-Path (Join-Path $installed 'unins000.exe'))) { throw 'Portable to machine conversion did not take over its original directory.' }
            'PASS portable to machine conversion takes over the same directory and preserves data'
        }
    }
    if ($Machine) {
        Run-Installer (Join-Path $installed 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $work 'uninstall.log') + '"'))
        if (Appx "(Get-AppxPackage -Name '$packageName').PackageFullName") { throw 'Uninstall left a package registration.' }
        if (Test-Path -LiteralPath (Join-Path $installed 'WindowsIsland.exe')) { throw 'Uninstall left the executable.' }
        'PASS isolated machine uninstall removes its files and current-user package registration'
    } else {
        & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $installed 'Unregister-Package.ps1') -InstallDirectory $installed
        if ($LASTEXITCODE -ne 0) { throw 'Portable unregister script failed.' }
        'PASS portable unregister completes without an uninstaller or elevation'
    }
} finally {
    if ((Appx 'Get-AppxPackage -Name WindowsIsland.Desktop | Select-Object PackageFullName,InstallLocation | ConvertTo-Json -Compress') -ne $originalRegistration) { throw 'Normal package registration changed.' }
    if ((Get-ItemProperty 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Run' -ErrorAction SilentlyContinue).WindowsIsland -ne $originalStartup) { throw 'Normal startup preference changed.' }
    foreach ($original in $originalProcesses) {
        $current = Get-Process -Id $original.Id -ErrorAction Stop
        if ($current.StartTime -ne $original.StartTime -or $current.Path -ne $original.Path) { throw 'A normal instance changed.' }
    }
    Write-Output "Verification output: $work"
}
