#Requires -Version 7.0
param([Parameter(Mandatory)][string]$ExpectedUser, [Parameter(Mandatory)][string]$WorkDirectory)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if ($env:GITHUB_ACTIONS -ne 'true' -or $ExpectedUser -notmatch '^IslandCheck[0-9a-f]{8}$' -or
        $identity.Name -ne "$env:COMPUTERNAME\$ExpectedUser" -or
        ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Expected the isolated CI standard user.'
    }
    # LoadUserProfile loads HKCU, but Start-Process still inherits the runner's environment.
    # Obtain profile variables from Windows for the actual token, not from the runner.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CheckUserEnvironment {
    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
'@
    $block = [IntPtr]::Zero
    if (![CheckUserEnvironment]::CreateEnvironmentBlock([ref]$block, $identity.AccessToken.DangerousGetHandle(), $false)) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    try {
        $offset = 0
        $profileVariables = @{}
        while ($true) {
            $entry = [Runtime.InteropServices.Marshal]::PtrToStringUni([IntPtr]::Add($block, $offset))
            if (!$entry) { break }
            $offset += ($entry.Length + 1) * 2
            $separator = $entry.IndexOf('=')
            if ($separator -gt 0) { $profileVariables[$entry.Substring(0, $separator)] = $entry.Substring($separator + 1) }
        }
        if (!$profileVariables['USERPROFILE']) { throw 'Windows did not provide the loaded user profile.' }
        foreach ($name in @('USERPROFILE','USERNAME','USERDOMAIN','HOMEDRIVE','HOMEPATH','APPDATA','LOCALAPPDATA')) {
            [Environment]::SetEnvironmentVariable($name, $profileVariables[$name], 'Process')
        }
    } finally { [void][CheckUserEnvironment]::DestroyEnvironmentBlock($block) }
    $profile = [IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\')
    $registeredProfile = (Get-ItemProperty -LiteralPath "HKLM:/SOFTWARE/Microsoft/Windows NT/CurrentVersion/ProfileList/$($identity.User.Value)").ProfileImagePath
    if ($profile -ne [Environment]::ExpandEnvironmentVariables($registeredProfile).TrimEnd('\')) {
        throw 'The process environment does not match the loaded user profile.'
    }
    # Local accounts without a configured home directory omit these in the environment block.
    if (!$env:HOMEDRIVE) { $env:HOMEDRIVE = [IO.Path]::GetPathRoot($profile).TrimEnd('\') }
    if (!$env:HOMEPATH) { $env:HOMEPATH = $profile.Substring([IO.Path]::GetPathRoot($profile).Length - 1) }
    # A new account has not had an Explorer logon to create its shell directories.
    foreach ($folder in @('ApplicationData','LocalApplicationData','StartMenu','Programs','DesktopDirectory')) {
        $path = [Environment]::GetFolderPath([Environment+SpecialFolder]$folder, [Environment+SpecialFolderOption]::Create)
        if (!$path -or !$path.StartsWith($profile + '\', [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $path -PathType Container)) {
            throw "The isolated user shell folder is unavailable or outside its profile: $folder ($path)"
        }
    }
    $env:APPDATA = [Environment]::GetFolderPath('ApplicationData')
    $env:LOCALAPPDATA = [Environment]::GetFolderPath('LocalApplicationData')
    $env:TEMP = $WorkDirectory
    $env:TMP = $WorkDirectory
    $programs = [Environment]::GetFolderPath('Programs')
    $shortcutPath = Join-Path $programs ('IslandCheck-' + [Guid]::NewGuid().ToString('N') + '.lnk')
    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = (Get-Process -Id $PID).Path
        $shortcut.Save()
        if (!(Test-Path -LiteralPath $shortcutPath -PathType Leaf)) { throw 'Cannot create a standard-user Start menu shortcut.' }
        $childPrograms = & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -NonInteractive -Command "[Environment]::GetFolderPath('Programs')"
        if ($LASTEXITCODE -ne 0 -or $childPrograms -ne $programs) { throw 'Child processes resolve a different Start menu.' }
    } finally {
        if ($shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        if ($shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
        if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath -Force }
    }
    Write-Output 'PASS isolated standard-user profile, shell folders, shortcut and child environment'
} finally { $identity.Dispose() }
