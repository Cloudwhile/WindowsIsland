#Requires -Version 7.0
param([string]$BaseRef = '9ab4db7', [string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/bridge/updater.patch'))
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$paths = @('src/WindowsIsland/Services/UpdateInstaller.cs','src/WindowsIsland/Services/UpdatePackage.cs',
    'src/WindowsIsland/Services/GitHubReleaseClient.cs','src/WindowsIsland/components/SettingsUpdates.cs',
    'src/WindowsIsland/Services/UpdateElevation.cs','src/WindowsIsland/Program.cs')
Push-Location $workspace
try {
    $diff = & git -c core.quotepath=false diff --no-ext-diff --binary $BaseRef -- $paths
    if ($LASTEXITCODE -ne 0) { throw 'Cannot compare the updater with the requested bridge base.' }
    # The bridge alone seeds a missing marker for old ZIP clients. Registry reads
    # distinguish the old installation once; they never gate an update download.
    $mode = @'
using Microsoft.Win32;

namespace WindowsIsland.Services;

internal static class InstallationMode
{
    internal const string PortableMarker = "portable.marker";
    public static bool IsPortable(string? directory = null)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory ?? AppContext.BaseDirectory));
        var marker = Path.Combine(directory, PortableMarker);
        if (File.Exists(marker)) return true;
        static bool Same(string? value, string path) => value is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)), path, StringComparison.OrdinalIgnoreCase);
        try
        {
            using var legacy = Registry.CurrentUser.OpenSubKey(@"Software\Cloudwhile\WindowsIsland\Installer");
            if (Same(legacy?.GetValue("InstallDirectory") as string, directory)) return false;
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var inno = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B397E71-1AA3-4F3B-911B-C08E0FAB484E}_is1");
                if (Same(inno?.GetValue("InstallLocation") as string, directory)) return false;
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        try { File.WriteAllText(marker, ""); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException) { }
        return true;
    }
}
'@
    $mode = $mode.Replace("`r`n", "`n").TrimEnd("`n").Split("`n")
    $patch = ($diff -join "`n") + "`n" + @"
diff --git a/src/WindowsIsland/Services/InstallationMode.cs b/src/WindowsIsland/Services/InstallationMode.cs
new file mode 100644
--- /dev/null
+++ b/src/WindowsIsland/Services/InstallationMode.cs
@@ -0,0 +1,$($mode.Count) @@
"@ + "`n" + (($mode | ForEach-Object { '+' + $_ }) -join "`n") + "`n"
    $output = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
    [IO.File]::WriteAllText($output, $patch, [Text.UTF8Encoding]::new($false))
    Write-Output $output
} finally { Pop-Location }
