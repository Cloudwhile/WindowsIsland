#Requires -Version 7.0
param([Parameter(Mandatory)][ValidateSet('Test-Inno.ps1','Test-MsiUpgrade.ps1')][string]$Script)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $administrator = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
$test = Join-Path $PSScriptRoot $Script
if (!$administrator) { & $test; return }
# Hosted Windows runners use a full administrator token. Run per-user installer
# checks with a filtered token of the same user, without changing product cleanup.
$log = Join-Path $PSScriptRoot "../artifacts/verification/standard-user-$Script.log"
New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
$body = @'
$ErrorActionPreference = 'Stop'
Start-Transcript -LiteralPath '__LOG__' -Force
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if (([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Installer checks require a standard user token.'
        }
    } finally { $identity.Dispose() }
    & '__TEST__'
} catch { Write-Output ($_ | Out-String); exit 1 }
finally { Stop-Transcript }
'@
$body = $body.Replace('__LOG__', $log.Replace("'", "''")).Replace('__TEST__', $test.Replace("'", "''"))
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class IslandStandardUserCheck
{
    public static int Run(string executable, string encoded, string directory)
    {
        IntPtr token = IntPtr.Zero, filtered = IntPtr.Zero, sid = IntPtr.Zero, label = IntPtr.Zero;
        IntPtr administrators = IntPtr.Zero, disabled = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x018B, out token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Open runner token");
            if (!ConvertStringSidToSidW("S-1-5-32-544", out administrators))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Resolve administrator group");
            disabled = Marshal.AllocHGlobal(Marshal.SizeOf<Label>());
            Marshal.StructureToPtr(new Label { Sid = administrators }, disabled, false);
            if (!CreateRestrictedToken(token, 1, 1, disabled, 0, IntPtr.Zero, 0, IntPtr.Zero, out filtered))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Filter runner token");
            if (!ConvertStringSidToSidW("S-1-16-8192", out sid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var integrity = new Label { Sid = sid, Attributes = 0x20 };
            label = Marshal.AllocHGlobal(Marshal.SizeOf<Label>());
            Marshal.StructureToPtr(integrity, label, false);
            if (!SetTokenInformation(filtered, 25, label, Marshal.SizeOf<Label>() + (int)GetLengthSid(sid)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Set medium integrity");
            var startup = new Startup { Size = Marshal.SizeOf<Startup>(), Flags = 1, Show = 0 };
            Process child;
            var command = new StringBuilder("\"" + executable + "\" -NoProfile -NonInteractive -EncodedCommand " + encoded);
            if (!CreateProcessWithTokenW(filtered, 0, executable, command, 0x08000000, IntPtr.Zero, directory, ref startup, out child))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Start standard-user checks");
            try
            {
                if (WaitForSingleObject(child.Handle, 900000) != 0) throw new TimeoutException("Installer checks timed out.");
                uint code;
                if (!GetExitCodeProcess(child.Handle, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return (int)code;
            }
            finally { CloseHandle(child.Thread); CloseHandle(child.Handle); }
        }
        finally
        {
            if (label != IntPtr.Zero) Marshal.FreeHGlobal(label);
            if (disabled != IntPtr.Zero) Marshal.FreeHGlobal(disabled);
            if (administrators != IntPtr.Zero) LocalFree(administrators);
            if (sid != IntPtr.Zero) LocalFree(sid);
            if (filtered != IntPtr.Zero) CloseHandle(filtered);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct Label { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Startup
    {
        public int Size; public string Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public short Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct Process { public IntPtr Handle, Thread; public uint Id, ThreadId; }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool CreateRestrictedToken(IntPtr token, uint flags, uint disableCount, IntPtr disable, uint deleteCount, IntPtr delete, uint restrictCount, IntPtr restrict, out IntPtr filtered);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSidToSidW(string text, out IntPtr sid);
    [DllImport("advapi32.dll")] static extern uint GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int information, IntPtr value, int size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessWithTokenW(IntPtr token, uint logon, string application, StringBuilder command, uint flags, IntPtr environment, string directory, ref Startup startup, out Process process);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
}
'@
$result = [IslandStandardUserCheck]::Run((Get-Process -Id $PID).Path, $encoded, (Get-Location).Path)
if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log }
if ($result -ne 0) { throw "Standard-user installer checks failed: $result" }
