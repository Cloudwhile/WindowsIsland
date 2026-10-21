namespace WindowsIsland.Plugins;

internal static class PluginFiles
{
    public static void Copy(string source, string destination)
    {
        PluginPaths.CheckTree(source);
        PluginPaths.CheckLinks(destination);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            PluginPaths.CheckLinks(target);
            if (!File.Exists(target)) File.Copy(file, target);
        }
        foreach (var directory in Directory.EnumerateDirectories(source)) Copy(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    public static void RemoveCode(string directory)
    {
        if (!Directory.Exists(directory)) return;
        PluginPaths.CheckTree(directory);
        var backup = Path.Combine(Path.GetDirectoryName(directory)!, ".remove-" + Guid.NewGuid().ToString("N"));
        var targets = new[] { directory, directory + ".bak" }.Where(Directory.Exists).ToArray();
        var recoverable = true;
        try
        {
            for (var index = 0; index < targets.Length; index++) Copy(targets[index], Path.Combine(backup, index.ToString()));
            try { foreach (var target in targets) PluginPaths.DeleteTree(target); }
            catch
            {
                recoverable = false;
                for (var index = 0; index < targets.Length; index++) Copy(Path.Combine(backup, index.ToString()), targets[index]);
                recoverable = true;
                throw;
            }
        }
        finally
        {
            if (recoverable)
            {
                try { PluginPaths.DeleteTree(backup); }
                catch (Exception error) { Services.Diagnostics.Capture(Services.DiagnosticArea.Application, error, "RemovePluginBackup"); }
            }
        }
    }
}
