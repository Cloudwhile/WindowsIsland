param([Parameter(Mandatory)][string]$InstallDirectory, [ValidateSet(0,1)][int]$Portable,
    [Parameter(Mandatory)][ValidateSet('LocalMachine','CurrentUser')][string]$Scope)
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Installation directory cannot be a reparse point.'
}
function Same-Directory([string]$candidate) {
    if (!$candidate) { return $false }
    try { return [IO.Path]::GetFullPath($candidate).TrimEnd('\') -ieq $directory } catch { return $false }
}
$legacy = @()
foreach ($hive in @([Microsoft.Win32.RegistryHive]$Scope)) {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
        try {
            $root = $registry.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall')
            if (!$root) { continue }
            try {
                foreach ($name in $root.GetSubKeyNames()) {
                    $entry = $root.OpenSubKey($name)
                    try {
                        if ($entry.GetValue('WindowsInstaller') -ne 1 -or $entry.GetValue('DisplayName') -notlike 'Windows Island*' -or
                            $name -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { continue }
                        $location = [string]$entry.GetValue('InstallLocation')
                        # Early per-user MSI releases wrote only this directory value.
                        if (!$location) {
                            $previous = $registry.OpenSubKey('Software\Cloudwhile\WindowsIsland\Installer')
                            try { if ($previous) { $location = [string]$previous.GetValue('InstallDirectory') } }
                            finally { if ($previous) { $previous.Dispose() } }
                        }
                        if (Same-Directory $location) { $legacy += $name }
                    } finally { $entry.Dispose() }
                }
            } finally { $root.Dispose() }
        } finally { $registry.Dispose() }
    }
}
$uninstaller = Join-Path $directory 'unins000.exe'
$convert = $Scope -eq 'LocalMachine' -and $Portable -eq 1 -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)
if (!$legacy.Count -and !$convert) { return }
$startupKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $false)
try { $startup = if ($startupKey) { $startupKey.GetValue('WindowsIsland') } else { $null } }
finally { if ($startupKey) { $startupKey.Dispose() } }
# Uninstallers own their registration. A full sibling snapshot protects data,
# settings, plugins and portable markers while they remove their managed files.
$backupRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$backup = Join-Path $backupRoot ('WindowsIsland-Migration-' + [Guid]::NewGuid().ToString('N'))
if ([IO.Path]::GetDirectoryName($backup) -ine $backupRoot) { throw 'Invalid migration backup.' }
foreach ($item in Get-ChildItem -LiteralPath $directory -Recurse -Force) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Migration does not follow reparse points.' }
}
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $directory -Force) { Copy-Item -LiteralPath $item.FullName -Destination $backup -Recurse -Force }
$completed = $false
try {
    foreach ($product in @($legacy | Select-Object -Unique)) {
        # This is the sole legacy MSI path, used only to remove the old product registration.
        $process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList @('/x',$product,'/qn','/norestart') -WindowStyle Hidden -PassThru
        try { $process.WaitForExit(); if ($process.ExitCode -notin 0,3010) { throw "Legacy removal failed: $($process.ExitCode)" } }
        finally { $process.Dispose() }
    }
    if ($convert) {
        $process = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CONVERT=1') -WindowStyle Hidden -PassThru
        try { $process.WaitForExit(); if ($process.ExitCode -ne 0) { throw 'Installation conversion was cancelled or failed.' } }
        finally { $process.Dispose() }
    }
    $completed = $true
} finally {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $backup -Force) {
        # Converted installations no longer own an uninstaller or a policy snapshot.
        if ($completed -and $convert -and ($item.Name -like 'unins*' -or $item.Name -eq 'installer-policy.json')) { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $directory -Recurse -Force
    }
    if ($Scope -eq 'CurrentUser' -and $startup -ceq ('"' + (Join-Path $directory 'WindowsIsland.exe') + '" --startup')) {
        $startupKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
        try { $startupKey.SetValue('WindowsIsland', $startup, 'String') } finally { $startupKey.Dispose() }
    }
    # Keep the complete snapshot if migration failed; never sacrifice data to cleanup.
    if ($completed) { Remove-Item -LiteralPath $backup -Recurse -Force }
}
