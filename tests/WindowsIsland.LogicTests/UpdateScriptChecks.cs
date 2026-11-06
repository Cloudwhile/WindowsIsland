using WindowsIsland.Services;

internal static class UpdateScriptChecks
{
    public static Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "installer-mode", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var portable in new[] { false, true })
            {
                if (portable) File.WriteAllText(Path.Combine(root, InstallationMode.PortableMarker), "");
                var info = UpdateInstaller.CreateStartInfo(Path.Combine(root, "WindowsIsland-1.0.0-win-x64.exe"), root);
                check(info.UseShellExecute && info.ArgumentList.Contains("/DIR=" + root)
                    && info.ArgumentList.Contains("/UPDATE=1") && info.ArgumentList.Contains("/VERYSILENT")
                    && info.ArgumentList.Contains("/PORTABLE=1") == portable
                    && info.ArgumentList.Contains("/CURRENTUSER") == portable,
                    "Both distribution modes use the same installer, preserve their directory and retain mode: " + portable);
            }
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
