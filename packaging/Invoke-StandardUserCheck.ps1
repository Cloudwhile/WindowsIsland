#Requires -Version 7.0
param([Parameter(Mandatory)][ValidateSet('Test-Inno.ps1','Test-MsiUpgrade.ps1')][string]$Script, [switch]$Probe)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $administrator = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
$test = Join-Path $PSScriptRoot $Script
if (!$administrator) { if (!$Probe) { & $test }; return }
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
if ($Probe) { $body = $body.Replace("& '" + $test.Replace("'", "''") + "'", "Write-Output 'PASS standard-user token'") }
$childScript = Join-Path (Split-Path $log) ('standard-user-' + [Guid]::NewGuid().ToString('N') + '.ps1')
[IO.File]::WriteAllText($childScript, $body)
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
public static class IslandStandardUserCheck
{
    public static int Run(string executable, string script, string directory)
    {
        IntPtr token = IntPtr.Zero, filtered = IntPtr.Zero;
        IntPtr administrators = IntPtr.Zero, disabled = IntPtr.Zero;
        Action restoreDesktop = null, restoreStation = null;
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
            var startup = new Startup { Size = Marshal.SizeOf<Startup>(), Flags = 1, Show = 0 };
            // Hosted runner desktops can grant access only to Administrators.
            // Let this same user initialize GUI libraries, then restore both ACLs.
            var station = GetProcessWindowStation();
            var desktop = GetThreadDesktop(GetCurrentThreadId());
            restoreStation = AllowUser(station, 0xF037F);
            restoreDesktop = AllowUser(desktop, 0xF01FF);
            startup.Desktop = ObjectName(station) + "\\" + ObjectName(desktop);
            Process child;
            // A restricted copy of our own primary token needs no alternate logon.
            var command = new StringBuilder("\"" + executable + "\" -NoProfile -NonInteractive -File \"" + script + "\"");
            SetErrorMode(0x8003);
            if (!CreateProcessAsUserW(filtered, executable, command, IntPtr.Zero, IntPtr.Zero, false,
                0x08000000, IntPtr.Zero, directory, ref startup, out child))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "Start standard-user checks: " + error);
            }
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
            try { restoreDesktop?.Invoke(); }
            finally { restoreStation?.Invoke(); }
            if (disabled != IntPtr.Zero) Marshal.FreeHGlobal(disabled);
            if (administrators != IntPtr.Zero) LocalFree(administrators);
            if (filtered != IntPtr.Zero) CloseHandle(filtered);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }
    static string ObjectName(IntPtr handle)
    {
        uint required;
        GetUserObjectInformationW(handle, 2, null, 0, out required);
        var name = new StringBuilder((int)required / 2);
        if (!GetUserObjectInformationW(handle, 2, name, required, out required))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Read runner desktop name");
        return name.ToString();
    }
    static Action AllowUser(IntPtr handle, int access)
    {
        uint information = 4, required;
        GetUserObjectSecurity(handle, ref information, null, 0, out required);
        var original = new byte[required];
        if (!GetUserObjectSecurity(handle, ref information, original, required, out required))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Read runner desktop ACL");
        var descriptor = new RawSecurityDescriptor(original, 0);
        using (var identity = WindowsIdentity.GetCurrent())
            descriptor.DiscretionaryAcl.InsertAce(descriptor.DiscretionaryAcl.Count,
                new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, access, identity.User, false, null));
        var updated = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(updated, 0);
        if (!SetUserObjectSecurity(handle, ref information, updated))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Allow runner user desktop access");
        return () => {
            if (!SetUserObjectSecurity(handle, ref information, original))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Restore runner desktop ACL");
        };
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
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessAsUserW(IntPtr token, string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref Startup startup, out Process process);
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetUserObjectSecurity(IntPtr handle, ref uint information, byte[] descriptor, uint size, out uint required);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetUserObjectSecurity(IntPtr handle, ref uint information, byte[] descriptor);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder value, uint size, out uint required);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
}
'@
try { $result = [IslandStandardUserCheck]::Run((Get-Process -Id $PID).Path, $childScript, (Get-Location).Path) }
finally { Remove-Item -LiteralPath $childScript -Force }
if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log }
if ($result -ne 0) { throw "Standard-user installer checks failed: $result" }
