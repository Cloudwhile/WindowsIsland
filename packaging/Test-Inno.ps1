#Requires -Version 7.0
param([string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'), [switch]$ElevatedUninstall)
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
    if ($item.Name -notlike 'verification-*') { Copy-Item -LiteralPath $item.FullName -Destination $payload -Recurse }
}
Copy-Item -LiteralPath (Join-Path $workspace 'LICENSE') -Destination $payload
$manifest = Join-Path $payload 'Initialization/AppxManifest.xml'
([IO.File]::ReadAllText($manifest)).Replace('WindowsIsland.Desktop', $packageName).Replace('CN=WindowsIsland', 'CN=IslandInnoTest') |
    Set-Content -LiteralPath $manifest -Encoding UTF8
foreach ($file in @('Uninstall.ps1','UninstallUser.ps1')) {
    ([IO.File]::ReadAllText((Join-Path $PSScriptRoot $file))).Replace('WindowsIsland.Desktop', $packageName) |
        Set-Content -LiteralPath (Join-Path $packaging $file) -Encoding UTF8
}
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'WindowsIsland.iss'))
$source = $source.Replace('#define AppName "Windows Island"', '#define AppName "' + $appName + '"')
$source = $source.Replace('8B397E71-1AA3-4F3B-911B-C08E0FAB484E', $appId)
$source = $source.Replace('Software\Cloudwhile\WindowsIsland\Installer', 'Software\Cloudwhile\InnoTest-' + $id)
$source = $source.Replace("{localappdata}\Programs\Windows Island", $installed)
[IO.File]::WriteAllText((Join-Path $packaging 'WindowsIsland.iss'), $source)
$installer = Join-Path $work 'WindowsIsland-0.0.0-test-win-x64.exe'
function Run-Installer([string]$executable, [string[]]$arguments, [switch]$Elevate) {
    $options = @{ FilePath=$executable; ArgumentList=$arguments; WindowStyle='Hidden'; PassThru=$true }
    if ($Elevate) { $options.Verb = 'RunAs' }
    $process = Start-Process @options
    try {
        if (!$process.WaitForExit(120000)) { throw "Installer timed out: $executable" }
        if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode)" }
    } finally { $process.Dispose() }
}
try {
    & $compiler '/Q' '/DAppVersion=0.0.0-test' "/DPublishDirectory=$payload" "/DPackagingDirectory=$packaging" "/DOutputDirectory=$work" (Join-Path $packaging 'WindowsIsland.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated Inno build failed.' }
    Run-Installer $installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="' + $installed + '"'),('/LOG="' + (Join-Path $work 'install.log') + '"'))
    foreach ($file in @('WindowsIsland.exe','WindowsIsland.TelegramHook.dll','Uninstall.ps1','UninstallUser.ps1','AppxManifest.xml','resources.pri','en-US/WindowsIsland.resources.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $installed $file))) { throw "Missing installed file: $file" }
    }
    $registration = Appx "(Get-AppxPackage -Name '$packageName').InstallLocation"
    if ($registration.TrimEnd('\') -ne $installed) { throw 'First registration did not match the isolated installation.' }
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) ($appName + '.lnk')
    if (!(Test-Path -LiteralPath $shortcut)) { throw 'Start menu shortcut missing.' }
    'PASS isolated first installation, original-user registration, payload and shortcut'
    # Upgrade uses the existing cleanup script and must preserve the managed DLL
    # until Inno replaces it, then register the same isolated package again.
    Run-Installer $installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="' + $installed + '"'),('/LOG="' + (Join-Path $work 'upgrade.log') + '"'))
    if ((Appx "(Get-AppxPackage -Name '$packageName').InstallLocation").TrimEnd('\') -ne $installed) { throw 'Upgrade registration failed.' }
    'PASS isolated reinstall and registration'
    Run-Installer (Join-Path $installed 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $work 'uninstall.log') + '"')) -Elevate:$ElevatedUninstall
    if (Appx "(Get-AppxPackage -Name '$packageName').PackageFullName") { throw 'Uninstall left a package registration.' }
    if (Test-Path -LiteralPath $shortcut) { throw 'Uninstall left the shortcut.' }
    if (Test-Path -LiteralPath (Join-Path $installed 'WindowsIsland.exe')) { throw 'Uninstall left the executable.' }
    foreach ($generated in @('AppxManifest.xml','resources.pri','WindowsIsland.TelegramHook.dll')) {
        if (Test-Path -LiteralPath (Join-Path $installed $generated)) { throw "Uninstall left a managed file: $generated" }
    }
    if ((Get-Content (Join-Path $work 'uninstall.log') -Raw) -match 'Cannot call|Runtime error|cleanup did not complete') { throw 'Uninstall reported a runtime or cleanup error.' }
    'PASS real Inno uninstall completes without unsupported original-user calls'
} finally {
    if ((Appx 'Get-AppxPackage -Name WindowsIsland.Desktop | Select-Object PackageFullName,InstallLocation | ConvertTo-Json -Compress') -ne $originalRegistration) {
        throw 'Normal package registration changed.'
    }
    if ((Get-ItemProperty 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Run' -ErrorAction SilentlyContinue).WindowsIsland -ne $originalStartup) {
        throw 'Normal startup preference changed.'
    }
    foreach ($original in $originalProcesses) {
        $current = Get-Process -Id $original.Id -ErrorAction Stop
        if ($current.StartTime -ne $original.StartTime -or $current.Path -ne $original.Path) { throw 'A normal instance changed.' }
    }
    Write-Output "Verification output: $work"
}
