param([Parameter(Mandatory)][string]$InstallDirectory)
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
# The old, elevated uninstaller transfers only its former application directory
# to the interactive user, so portable file replacement itself remains unelevated.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class IslandConversionUser {
    public static string Sid() {
        uint process; var window = GetShellWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out process) == 0) throw new InvalidOperationException("No interactive shell.");
        var handle = OpenProcess(0x1000, false, process);
        IntPtr token = IntPtr.Zero;
        try {
            if (handle == IntPtr.Zero || !OpenProcessToken(handle, 8, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (var identity = new WindowsIdentity(token)) return identity.User.Value;
        } finally { if (token != IntPtr.Zero) CloseHandle(token); if (handle != IntPtr.Zero) CloseHandle(handle); }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
'@
New-Item -ItemType Directory -Path $directory -Force | Out-Null
if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Conversion cannot follow a reparse point.' }
$sid = [Security.Principal.SecurityIdentifier]::new([IslandConversionUser]::Sid())
$acl = Get-Acl -LiteralPath $directory
$rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.SetAccessRule($rule)
Set-Acl -LiteralPath $directory -AclObject $acl
