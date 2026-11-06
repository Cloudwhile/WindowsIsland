#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$id = [Guid]::NewGuid().ToString('N')
$registryPath = 'Software\Cloudwhile\InstallerPolicyChecks\' + $id
$work = Join-Path $PSScriptRoot ('../artifacts/verification/installer-policy/' + $id)
New-Item -ItemType Directory -Path $work -Force | Out-Null
$machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
$key = $machine.CreateSubKey($registryPath)
try {
    $key.SetValue('AllowAllTrustedApps', 0, 'DWord')
    $snapshot = Join-Path $work 'snapshot.json'
    & (Join-Path $PSScriptRoot 'InstallerPolicy.ps1') -Action Prepare -SnapshotPath $snapshot -RegistryPath $registryPath -RegistryHive CurrentUser
    if ($key.GetValue('AllowAllTrustedApps') -ne 1 -or $key.GetValue('AllowDevelopmentWithoutDevLicense') -ne 1) { throw 'Both policy values must be prepared.' }
    $original = [IO.File]::ReadAllText($snapshot)
    & (Join-Path $PSScriptRoot 'InstallerPolicy.ps1') -Action Prepare -SnapshotPath $snapshot -RegistryPath $registryPath -RegistryHive CurrentUser
    if ([IO.File]::ReadAllText($snapshot) -cne $original) { throw 'An upgrade overwrote the original snapshot.' }
    & (Join-Path $PSScriptRoot 'InstallerPolicy.ps1') -Action Restore -SnapshotPath $snapshot -RegistryPath $registryPath -RegistryHive CurrentUser
    if ($key.GetValue('AllowAllTrustedApps') -ne 0 -or $key.GetValue('AllowDevelopmentWithoutDevLicense') -ne 1) { throw 'Restore must recover old values and preserve newly created values.' }
    $key.SetValue('AllowAllTrustedApps', 7, 'DWord')
    & (Join-Path $PSScriptRoot 'InstallerPolicy.ps1') -Action Restore -SnapshotPath $snapshot -RegistryPath $registryPath -RegistryHive CurrentUser
    if ($key.GetValue('AllowAllTrustedApps') -ne 7) { throw 'A later external policy change was overwritten.' }
    & (Join-Path $PSScriptRoot 'InstallerPolicy.ps1') -Action Restore -SnapshotPath (Join-Path $work 'missing.json') -RegistryPath $registryPath -RegistryHive CurrentUser
    if ($key.GetValue('AllowAllTrustedApps') -ne 7 -or $key.GetValueNames().Count -ne 2) { throw 'A missing snapshot must never mutate policies.' }
    'PASS policy preparation, upgrade snapshot, selective restore, absent values, external changes and missing snapshots'
} finally {
    $key.Dispose()
    # Only this GUID test subtree is removed; production code never deletes policy keys.
    $machine.DeleteSubKeyTree($registryPath)
    $machine.Dispose()
}
