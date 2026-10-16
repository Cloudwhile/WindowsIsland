param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'),
    [switch]$InspectOnly,
    [switch]$SkipLaunch
)
$ErrorActionPreference = 'Stop'
$msi = (Resolve-Path -LiteralPath $InstallerPath).Path
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$directory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/msi-smoke/installed'))
$logs = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/verification/msi'))
$registration = 'HKCU:\Software\Cloudwhile\WindowsIsland\Installer'
function Get-MsiTable([string]$table) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msi, 0))
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @("SELECT * FROM $table"))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
    $rows = @()
    while ($record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)) {
        $count = $record.GetType().InvokeMember('FieldCount', 'GetProperty', $null, $record, $null)
        $row = @()
        for ($index = 1; $index -le $count; $index++) {
            $row += $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @($index))
        }
        $rows += ,$row
    }
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
    return $rows
}
$properties = @{}
foreach ($row in @(Get-MsiTable 'Property')) { $properties[$row[0]] = $row[1] }
if ($properties.ALLUSERS -ne '2' -or $properties.MSIINSTALLPERUSER -ne '1') {
    throw 'The MSI must default to a non-elevated current-user installation while retaining elevation support.'
}
if ($properties.WIXUI_INSTALLDIR -ne 'INSTALLFOLDER') { throw 'The install directory UI is not bound to INSTALLFOLDER.' }
$installDirectoryControls = @(Get-MsiTable 'Control') | Where-Object { $_[0] -eq 'InstallDirDlg' }
if (!($installDirectoryControls | Where-Object { $_[1] -eq 'Folder' -and $_[2] -eq 'PathEdit' -and $_[8] -eq 'WIXUI_INSTALLDIR' })) {
    throw 'The MSI does not contain an editable installation directory control.'
}
if (!($installDirectoryControls | Where-Object { $_[1] -eq 'ChangeFolder' -and $_[2] -eq 'PushButton' })) {
    throw 'The MSI does not contain the installation directory browser button.'
}
$installDirectoryEvents = @(Get-MsiTable 'ControlEvent') | Where-Object { $_[0] -eq 'InstallDirDlg' -and $_[1] -eq 'Next' }
if (!($installDirectoryEvents | Where-Object { $_[2] -eq 'DoAction' -and $_[3] -eq 'SelectInstallScope' })) {
    throw 'The MSI does not check whether the selected installation directory requires elevation.'
}
$uiSequence = @(Get-MsiTable 'InstallUISequence')
if (!($uiSequence | Where-Object { $_[0] -eq 'SelectInstallScope' })) {
    throw 'The MSI does not check the selected directory before the full-UI execution handoff.'
}
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msi, 0))
$summary = $database.GetType().InvokeMember('SummaryInformation', 'GetProperty', $null, $database, @(0))
$wordCount = $summary.GetType().InvokeMember('Property', 'GetProperty', $null, $summary, @(15))
if (([int]$wordCount -band 8) -ne 0) { throw 'The MSI forbids elevation and would fail for protected installation directories.' }
'PASS MSI exposes an editable installation directory and defaults to current-user scope.'
if ($InspectOnly) { return }
if (Test-Path $registration) { throw 'An existing MSI installation must not be replaced by this check.' }
if (Test-Path -LiteralPath $directory) { throw 'The MSI check needs an unused installation directory.' }
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$installed = $false
$application = $null
try {
    $arguments = @('/i', ('"{0}"' -f $msi), '/qn', '/norestart', ('INSTALLFOLDER="{0}"' -f $directory), '/L*v', ('"{0}"' -f (Join-Path $logs 'install.log')))
    $process = Start-Process msiexec.exe -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -notin @(0,3010)) { throw "MSI installation failed: $($process.ExitCode)" }
    $installed = $true
    if ((Get-ItemPropertyValue -Path $registration -Name InstallDirectory) -ne ($directory.TrimEnd('\') + '\')) {
        throw 'The MSI did not persist the selected installation directory.'
    }
    foreach ($source in @(Get-ChildItem -LiteralPath $publish -File -Recurse)) {
        $relative = [IO.Path]::GetRelativePath($publish,$source.FullName)
        $target = Join-Path $directory $relative
        if (!(Test-Path -LiteralPath $target)) { throw "Missing installed file: $relative" }
        if ((Get-FileHash -LiteralPath $source.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            throw "Installed file mismatch: $relative"
        }
    }
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Windows Island/Windows Island.lnk'
    if (!(Test-Path -LiteralPath $shortcut)) { throw 'The Start menu shortcut is missing.' }
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($shortcut).TargetPath -ne (Join-Path $directory 'WindowsIsland.exe')) { throw 'The Start menu shortcut has the wrong target.' }
    if (!$SkipLaunch) {
        $application = Start-Process (Join-Path $directory 'WindowsIsland.exe') -ArgumentList '--settings' -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 5
        if ($application.HasExited) { throw 'The installed application exited during startup.' }
    }
    'PASS MSI installs the complete application and its Start menu shortcut.'
} finally {
    if ($application -and !$application.HasExited) {
        if ($application.Path -ne (Join-Path $directory 'WindowsIsland.exe')) { throw 'Unexpected application process.' }
        Stop-Process -InputObject $application
        [void]$application.WaitForExit(5000)
    }
    if ($installed) {
        $arguments = @('/x', ('"{0}"' -f $msi), '/qn', '/norestart', '/L*v', ('"{0}"' -f (Join-Path $logs 'uninstall.log')))
        $process = Start-Process msiexec.exe -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
        if ($process.ExitCode -notin @(0,3010)) { throw "MSI uninstall failed: $($process.ExitCode)" }
    }
}
if (Test-Path -LiteralPath (Join-Path $directory 'WindowsIsland.exe')) { throw 'MSI uninstall left the application installed.' }
if (Test-Path -LiteralPath $shortcut) { throw 'MSI uninstall left a Start menu shortcut.' }
if ((Get-ItemProperty -Path $registration -Name InstallDirectory -ErrorAction SilentlyContinue).InstallDirectory) { throw 'MSI uninstall left its installation registration.' }
'PASS MSI uninstalls the application and its Start menu shortcut.'
