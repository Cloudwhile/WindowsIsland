#Requires -Version 7.0
param([Parameter(Mandatory)][ValidateSet('Test-Inno.ps1','Test-MsiUpgrade.ps1')][string]$Script, [switch]$Probe)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $administrator = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
$test = Join-Path $PSScriptRoot $Script
if (!$administrator) { if (!$Probe) { & $test }; return }
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Run local installer checks from a standard user session.' }
$user = 'IslandCheck' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$secure = ConvertTo-SecureString ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(36)) + 'aA1!') -AsPlainText -Force
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$work = Join-Path $root ('artifacts/verification/user-' + $user)
New-Item -ItemType Directory -Path $work -Force | Out-Null
$child = Join-Path $work 'check.ps1'
$action = if ($Probe) { "Write-Output 'PASS standard-user logon'" } else { "& '" + $test.Replace("'", "''") + "'" }
$lines = @(
    '$ErrorActionPreference = ''Stop''',
    ('$env:TEMP = ''' + $work.Replace("'", "''") + ''''),
    '$env:TMP = $env:TEMP',
    ('Start-Transcript -LiteralPath ''' + (Join-Path $work 'check.log').Replace("'", "''") + ''' -Force'),
    'try {',
    '  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()',
    '  try { if (([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw ''Expected standard user.'' } } finally { $identity.Dispose() }',
    $action,
    '} catch { Write-Output ($_ | Out-String); exit 1 } finally { Stop-Transcript }'
)
[IO.File]::WriteAllLines($child, $lines)
try {
    New-LocalUser -Name $user -Password $secure -AccountNeverExpires -PasswordNeverExpires | Out-Null
    & icacls.exe $root /grant "${user}:(OI)(CI)M" /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot grant isolated user workspace access.' }
    $credential = [pscredential]::new("$env:COMPUTERNAME\$user", $secure)
    $process = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile','-NonInteractive','-File',('"' + $child + '"')) -Credential $credential -LoadUserProfile -WorkingDirectory $root -WindowStyle Hidden -PassThru
    try {
        if (!$process.WaitForExit(900000)) { throw 'Installer checks timed out.' }
        $result = $process.ExitCode
    } finally { $process.Dispose() }
    if (Test-Path (Join-Path $work 'check.log')) { Get-Content (Join-Path $work 'check.log') }
    if ($result -ne 0) { throw "Isolated-user installer checks failed: $result" }
} finally {
    & icacls.exe $root /remove:g $user /T /Q | Out-Null
    Remove-LocalUser -Name $user -ErrorAction SilentlyContinue
}
