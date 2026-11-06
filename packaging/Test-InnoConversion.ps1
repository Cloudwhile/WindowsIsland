#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$id = [Guid]::NewGuid().ToString('N')
$work = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../artifacts/verification/inno-conversion/$id"))
$directory = Join-Path $work 'installed'
$other = Join-Path $work 'other'
$key = "Software\Cloudwhile\InstallerChecks\$id"
$scriptFile = Join-Path $work 'Convert-InnoInstallation.ps1'
New-Item -ItemType Directory -Path $directory,$other -Force | Out-Null
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Convert-InnoInstallation.ps1'))
$source = $source.Replace("OpenBaseKey('LocalMachine', 'Registry64')", "OpenBaseKey('CurrentUser', 'Registry64')")
$source = $source.Replace('Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B397E71-1AA3-4F3B-911B-C08E0FAB484E}_is1', $key)
$source = $source.Replace('[IO.Path]::GetTempPath()', ("'" + $work.Replace("'", "''") + "'"))
[IO.File]::WriteAllText($scriptFile, $source)
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
$state = @{ Invocations = 0; Failure = $false }
function Check([bool]$value, [string]$message) {
    if (!$value) { throw $message }
    Write-Output "PASS $message"
}
function Register-Directory([string]$location) {
    $entry = $registry.CreateSubKey($key)
    try { $entry.SetValue('InstallLocation', $location) } finally { $entry.Dispose() }
}
function Expect-Rejection([string]$message) {
    $rejected = $false
    try { & $scriptFile -InstallDirectory $directory } catch { $rejected = $true }
    Check ($rejected -and $state.Invocations -eq 0) $message
}
# This double exercises conversion decisions and snapshot recovery, without
# launching an elevated uninstaller or touching product registration.
function Start-Process {
    param([string]$FilePath, [string[]]$ArgumentList, [string]$WindowStyle, [switch]$PassThru)
    if ($FilePath -cne (Join-Path $directory 'unins000.exe') -or $ArgumentList -notcontains '/CONVERT=1') {
        throw 'Conversion must invoke only the matched Inno uninstaller.'
    }
    $state.Invocations++
    Remove-Item -LiteralPath (Join-Path $directory 'settings.json')
    Remove-Item -LiteralPath (Join-Path $directory 'unins000.exe')
    Remove-Item -LiteralPath (Join-Path $directory 'installer-policy.json')
    if (!$state.Failure) { $registry.DeleteSubKeyTree($key, $false) }
    $process = [pscustomobject]@{ ExitCode = [int]$state.Failure }
    $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { }
    $process | Add-Member -MemberType ScriptMethod -Name Dispose -Value { }
    return $process
}
try {
    [IO.File]::WriteAllText((Join-Path $directory 'settings.json'), 'preserved settings')
    & $scriptFile -InstallDirectory $directory
    Check ($state.Invocations -eq 0 -and [IO.File]::ReadAllText((Join-Path $directory 'settings.json')) -ceq 'preserved settings') 'A portable directory needs no conversion'
    [IO.File]::WriteAllText((Join-Path $directory 'unins000.exe'), 'isolated double')
    Expect-Rejection 'An unregistered uninstaller cannot be invoked'
    Register-Directory $other
    Expect-Rejection 'A different registered directory cannot be converted'
    Register-Directory $directory
    Remove-Item -LiteralPath (Join-Path $directory 'unins000.exe')
    Expect-Rejection 'A matched registration with a missing uninstaller fails'
    [IO.File]::WriteAllText((Join-Path $directory 'unins000.exe'), 'isolated double')
    [IO.File]::WriteAllText((Join-Path $directory 'installer-policy.json'), 'isolated policy snapshot')
    $link = Join-Path $directory 'linked-data'
    New-Item -ItemType Junction -Path $link -Target $other | Out-Null
    try { Expect-Rejection 'Conversion rejects linked content before invoking an uninstaller' }
    finally { [IO.Directory]::Delete($link) }
    & $scriptFile -InstallDirectory $directory
    Check ($state.Invocations -eq 1 -and [IO.File]::ReadAllText((Join-Path $directory 'settings.json')) -ceq 'preserved settings' -and
        !(Test-Path -LiteralPath (Join-Path $directory 'unins000.exe')) -and
        !(Test-Path -LiteralPath (Join-Path $directory 'installer-policy.json')) -and
        @(Get-ChildItem -LiteralPath $work -Directory -Filter 'WindowsIsland-Conversion-*').Count -eq 0) 'Successful conversion restores data while discarding installation metadata and its snapshot'
    Register-Directory $directory
    [IO.File]::WriteAllText((Join-Path $directory 'unins000.exe'), 'isolated double')
    [IO.File]::WriteAllText((Join-Path $directory 'installer-policy.json'), 'isolated policy snapshot')
    $state.Failure = $true
    $rejected = $false
    try { & $scriptFile -InstallDirectory $directory } catch { $rejected = $true }
    Check ($rejected -and $state.Invocations -eq 2 -and
        [IO.File]::ReadAllText((Join-Path $directory 'settings.json')) -ceq 'preserved settings' -and
        (Test-Path -LiteralPath (Join-Path $directory 'unins000.exe')) -and
        (Test-Path -LiteralPath (Join-Path $directory 'installer-policy.json')) -and
        @(Get-ChildItem -LiteralPath $work -Directory -Filter 'WindowsIsland-Conversion-*').Count -eq 1) 'Failed conversion restores all files and retains a complete recovery snapshot'
} finally {
    $registry.DeleteSubKeyTree($key, $false)
    $registry.Dispose()
    Write-Output "Verification output: $work"
}
