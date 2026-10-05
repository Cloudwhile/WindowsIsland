param([Parameter(Mandatory)][string]$InstallDirectory)
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$executable = Join-Path $directory 'WindowsIsland.exe'
foreach ($application in @(Get-Process WindowsIsland -ErrorAction SilentlyContinue)) {
    if ($application.Path -eq $executable) {
        Stop-Process -InputObject $application
        if (!$application.WaitForExit(5000)) { throw 'Windows Island could not be stopped.' }
    }
}
$startupKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$approvalKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$startup = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($startupKey, $true)
try {
    if ($startup -and $startup.GetValue('WindowsIsland') -eq ('"' + $executable + '" --startup')) {
        $startup.DeleteValue('WindowsIsland', $false)
        $approval = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($approvalKey, $true)
        try { if ($approval) { $approval.DeleteValue('WindowsIsland', $false) } }
        finally { if ($approval) { $approval.Dispose() } }
    }
} finally { if ($startup) { $startup.Dispose() } }
foreach ($package in @(Get-AppxPackage -Name WindowsIsland.Desktop)) {
    if ($package.InstallLocation.TrimEnd('\') -eq $directory) {
        Remove-AppxPackage -Package $package.PackageFullName -PreserveApplicationData
    }
}
