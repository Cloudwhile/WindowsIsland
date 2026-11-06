param([Parameter(Mandatory)][string]$InstallDirectory)
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Installation directory cannot be a reparse point.'
}
$uninstaller = Join-Path $directory 'unins000.exe'
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
try {
    $entry = $registry.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B397E71-1AA3-4F3B-911B-C08E0FAB484E}_is1')
    try {
        if (!$entry) {
            if (Test-Path -LiteralPath $uninstaller) { throw 'The uninstaller does not belong to this installation.' }
            return
        }
        $location = [string]$entry.GetValue('InstallLocation')
        if (!$location -or [IO.Path]::GetFullPath($location).TrimEnd('\') -ine $directory) {
            if (Test-Path -LiteralPath $uninstaller) { throw 'The uninstaller does not match the registered installation directory.' }
            return
        }
        if (!(Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'The installation uninstaller is missing.' }
    } finally { if ($entry) { $entry.Dispose() } }
} finally { $registry.Dispose() }

# The installed uninstaller owns machine registration. Retain all files until
# it has removed that registration and prepared the directory for portable use.
foreach ($item in Get-ChildItem -LiteralPath $directory -Recurse -Force) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Conversion does not follow reparse points.' }
}
$backupRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$backup = Join-Path $backupRoot ('WindowsIsland-Conversion-' + [Guid]::NewGuid().ToString('N'))
if ([IO.Path]::GetDirectoryName($backup) -ine $backupRoot) { throw 'Invalid conversion backup.' }
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $backup -Recurse -Force
}
$completed = $false
try {
    $process = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CONVERT=1') -WindowStyle Hidden -PassThru
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw 'Installation conversion was cancelled or failed.' }
    } finally { $process.Dispose() }
    $completed = $true
} finally {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $backup -Force) {
        if ($completed -and ($item.Name -like 'unins*' -or $item.Name -eq 'installer-policy.json')) { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $directory -Recurse -Force
    }
    # Failed conversion retains the complete snapshot for recovery.
    if ($completed) { Remove-Item -LiteralPath $backup -Recurse -Force }
}
