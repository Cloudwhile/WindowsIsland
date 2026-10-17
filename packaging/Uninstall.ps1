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
function Remove-LegacyHookCache {
    $hooks = Join-Path $directory 'Hooks'
    if (!(Test-Path -LiteralPath $hooks -PathType Container)) { return }
    $root = Get-Item -LiteralPath $hooks -Force
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
    foreach ($hashDirectory in @(Get-ChildItem -LiteralPath $hooks -Force)) {
        if (!$hashDirectory.PSIsContainer -or $hashDirectory.Name -notmatch '^[0-9A-F]{64}$' -or
            ($hashDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
        foreach ($item in @(Get-ChildItem -LiteralPath $hashDirectory.FullName -Force)) {
            if ($item.PSIsContainer -or $item.Name -cne 'WindowsIsland.TelegramHook.dll' -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
        }
    }
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try { Remove-Item -LiteralPath $hooks -Recurse -Force; return }
        catch [IO.IOException] { if ($attempt -eq 49) { throw }; Start-Sleep -Milliseconds 100 }
        catch [UnauthorizedAccessException] { if ($attempt -eq 49) { throw }; Start-Sleep -Milliseconds 100 }
    }
}
Remove-LegacyHookCache
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
