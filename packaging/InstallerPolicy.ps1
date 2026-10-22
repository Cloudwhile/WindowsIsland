param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Restore')][string]$Action,
    [Parameter(Mandatory)][string]$SnapshotPath,
    [string]$RegistryPath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock',
    [ValidateSet('LocalMachine','CurrentUser')][string]$RegistryHive = 'LocalMachine'
)
$ErrorActionPreference = 'Stop'
$names = @('AllowAllTrustedApps','AllowDevelopmentWithoutDevLicense')
$machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey($RegistryHive, 'Registry64')
try {
    if ($Action -eq 'Prepare') {
        $key = $machine.CreateSubKey($RegistryPath, $true)
        try {
            # Commit the original values before the first mutation; upgrades reuse this snapshot.
            if (!(Test-Path -LiteralPath $SnapshotPath)) {
                $snapshot = foreach ($name in $names) {
                    $exists = $key.GetValueNames() -contains $name
                    $kind = if ($exists) { $key.GetValueKind($name).ToString() } else { 'DWord' }
                    $value = if ($exists) { $key.GetValue($name, $null, 'DoNotExpandEnvironmentNames') } else { $null }
                    [ordered]@{ Name=$name; Exists=$exists; Kind=$kind; Value=$value; Changed=(!$exists -or $kind -ne 'DWord' -or $value -ne 1) }
                }
                $temporary = $SnapshotPath + '.tmp'
                [IO.File]::WriteAllText($temporary, (ConvertTo-Json -InputObject @($snapshot) -Depth 4), [Text.UTF8Encoding]::new($false))
                Move-Item -LiteralPath $temporary -Destination $SnapshotPath
            }
            foreach ($name in $names) { $key.SetValue($name, 1, 'DWord') }
        } finally { $key.Dispose() }
    } else {
        # Missing snapshots never authorize changes. Missing original values remain present.
        if (!(Test-Path -LiteralPath $SnapshotPath -PathType Leaf)) { return }
        $snapshot = @(Get-Content -LiteralPath $SnapshotPath -Raw -Encoding UTF8 | ConvertFrom-Json)
        $key = $machine.OpenSubKey($RegistryPath, $true)
        if (!$key) { return }
        try {
            foreach ($entry in $snapshot) {
                if ($entry.Name -cnotin $names -or $entry.Changed -ne $true -or $entry.Exists -ne $true) { continue }
                # Do not overwrite later user or policy changes.
                if ($key.GetValueNames() -notcontains $entry.Name -or $key.GetValueKind($entry.Name) -ne 'DWord' -or $key.GetValue($entry.Name) -ne 1) { continue }
                $kind = [Microsoft.Win32.RegistryValueKind]$entry.Kind
                $value = switch ($kind) {
                    'DWord' { [int]$entry.Value }
                    'QWord' { [long]$entry.Value }
                    'Binary' { [byte[]]$entry.Value }
                    'MultiString' { ,([string[]]$entry.Value) }
                    default { [string]$entry.Value }
                }
                $key.SetValue($entry.Name, $value, $kind)
            }
        } finally { $key.Dispose() }
    }
} finally { $machine.Dispose() }
