param([Parameter(Mandatory)][string]$InstallDirectory)
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Installation directory cannot be a reparse point.'
}
$executable = Join-Path $directory 'WindowsIsland.exe'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IslandCloseWindows {
    private delegate bool Callback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr w, IntPtr l);
    public static void Request(uint process) {
        EnumWindows((window, parameter) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == process) PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }
}
'@
foreach ($application in @(Get-Process WindowsIsland -ErrorAction SilentlyContinue)) {
    if ($application.Path -ine $executable) { continue }
    try { [void]$application.CloseMainWindow() } catch { }
    [IslandCloseWindows]::Request([uint32]$application.Id)
    if (!$application.WaitForExit(5000)) {
        Stop-Process -InputObject $application -Force
        if (!$application.WaitForExit(5000)) { throw 'Windows Island could not be stopped.' }
    }
}
