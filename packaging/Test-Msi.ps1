param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'),
    [switch]$SkipLaunch
)
$ErrorActionPreference = 'Stop'
$msi = (Resolve-Path -LiteralPath $InstallerPath).Path
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$directory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/msi-smoke/installed'))
$logs = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/verification/msi'))
$registration = 'HKCU:\Software\Cloudwhile\WindowsIsland\Installer'
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
