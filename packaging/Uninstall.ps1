param([Parameter(Mandatory)][string]$InstallDirectory, [switch]$PreserveManagedHook)
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$root = Get-Item -LiteralPath $directory -Force
if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Installation directory cannot be a reparse point.'
}
& (Join-Path $PSScriptRoot 'Close-InstalledApp.ps1') -InstallDirectory $directory
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
function Remove-PackageMetadata {
    $metadata = Join-Path $directory 'microsoft.system.package.metadata'
    if (!(Test-Path -LiteralPath $metadata -PathType Container)) { return }
    try {
        $root = Get-Item -LiteralPath $metadata -Force
        if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
        # Package removal owns the contents. This script only removes the
        # directory after confirming it is empty, so unrelated metadata cannot
        # be recursively deleted from an installation directory.
        if (@(Get-ChildItem -LiteralPath $metadata -Force).Count -ne 0) { return }
    } catch [IO.IOException] { return }
    catch [UnauthorizedAccessException] { return }
    catch [System.Security.SecurityException] { return }
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try { [IO.Directory]::Delete($metadata, $false); return }
        catch [IO.IOException] { if ($attempt -eq 49) { return }; Start-Sleep -Milliseconds 100 }
        catch [UnauthorizedAccessException] { if ($attempt -eq 49) { return }; Start-Sleep -Milliseconds 100 }
    }
}
function Remove-ManagedHookBinary {
    $hook = Join-Path $directory 'WindowsIsland.TelegramHook.dll'
    if (!(Test-Path -LiteralPath $hook -PathType Leaf)) { return }
    try {
        $item = Get-Item -LiteralPath $hook -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
    } catch [IO.IOException] { return }
    catch [UnauthorizedAccessException] { return }
    catch [System.Security.SecurityException] { return }
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try { Remove-Item -LiteralPath $hook -Force; return }
        catch [IO.IOException] { if ($attempt -eq 49) { throw 'Telegram hook DLL could not be removed.' }; Start-Sleep -Milliseconds 100 }
        catch [UnauthorizedAccessException] { if ($attempt -eq 49) { throw 'Telegram hook DLL could not be removed.' }; Start-Sleep -Milliseconds 100 }
    }
}
& (Join-Path $PSScriptRoot 'UninstallUser.ps1') -InstallDirectory $directory
Remove-PackageMetadata
if (!$PreserveManagedHook) { Remove-ManagedHookBinary }
