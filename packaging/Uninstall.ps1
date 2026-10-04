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
foreach ($package in @(Get-AppxPackage -Name WindowsIsland.Desktop)) {
    if ($package.InstallLocation.TrimEnd('\') -eq $directory) {
        Remove-AppxPackage -Package $package.PackageFullName -PreserveApplicationData
    }
}
