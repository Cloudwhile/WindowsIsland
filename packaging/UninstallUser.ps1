param([Parameter(Mandatory)][string]$InstallDirectory, [switch]$UserContext)
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Installation directory cannot be a reparse point.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $elevated = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
if ($elevated) {
    if ($UserContext) { throw 'User cleanup must not run with an elevated token.' }
    # Inno's original-user API does not exist during uninstall. Use the shell
    # in this desktop session, and wait for its non-elevated cleanup to finish.
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class IslandUninstallUser
{
    public static void Run(string script, string directory)
    {
        uint processId;
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out processId) == 0)
            throw new InvalidOperationException("The interactive user shell is unavailable.");
        using (var shellProcess = Process.GetProcessById((int)processId))
        using (var current = Process.GetCurrentProcess())
        {
            if (shellProcess.SessionId != current.SessionId)
                throw new InvalidOperationException("The shell belongs to another session.");
            IntPtr token = IntPtr.Zero, primary = IntPtr.Zero, shellHandle = IntPtr.Zero;
            try
            {
                shellHandle = OpenProcess(0x1000, false, processId);
                if (shellHandle == IntPtr.Zero || !OpenProcessToken(shellHandle, 0x000A, out token)
                    || !DuplicateTokenEx(token, 0x018B, IntPtr.Zero, 2, 1, out primary))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var executable = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe");
                // Windows paths cannot contain quotes. Terminate the directory
                // with a dot to avoid a trailing slash escaping the quote.
                var command = new StringBuilder("\"" + executable + "\" -NoProfile -NonInteractive -WindowStyle Hidden"
                    + " -ExecutionPolicy Bypass -File \"" + script + "\" -InstallDirectory \"" + directory + "\\.\" -UserContext");
                var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Flags = 1, Show = 0 };
                ProcessInfo child;
                if (!CreateProcessWithTokenW(primary, 1, executable, command, 0x08000000,
                    IntPtr.Zero, Environment.GetFolderPath(Environment.SpecialFolder.System), ref startup, out child))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (WaitForSingleObject(child.Process, 60000) != 0)
                        throw new TimeoutException("User cleanup did not finish.");
                    uint code;
                    if (!GetExitCodeProcess(child.Process, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (code != 0) throw new InvalidOperationException("User cleanup failed.");
                }
                finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
            }
            finally
            {
                if (primary != IntPtr.Zero) CloseHandle(primary);
                if (token != IntPtr.Zero) CloseHandle(token);
                if (shellHandle != IntPtr.Zero) CloseHandle(shellHandle);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public short Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithTokenW(IntPtr token, uint logon, string application, StringBuilder command, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
'@
    [IslandUninstallUser]::Run($PSCommandPath, $directory)
    return
}
$executable = Join-Path $directory 'WindowsIsland.exe'
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
