using System.Runtime.CompilerServices;
using WindowsIsland.Plugins;

internal static class PluginErrorChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        if (Environment.GetCommandLineArgs().Length != 1) return;
        var codes = new[] { PluginErrorCode.Zip, PluginErrorCode.Manifest, PluginErrorCode.Id,
            PluginErrorCode.Contract, PluginErrorCode.Entry, PluginErrorCode.Downgrade,
            PluginErrorCode.Version, PluginErrorCode.Path, PluginErrorCode.SourceConflict,
            PluginErrorCode.Start, PluginErrorCode.SourceAddress, PluginErrorCode.Hash,
            PluginErrorCode.IndexMismatch, PluginErrorCode.NotListed };
        foreach (var code in codes)
        {
            foreach (var message in new[] { "已经修改的说明", "A localized message", "" })
                if (PluginErrors.Key(new PluginException(code, message)) != "PluginError" + code)
                    throw new InvalidOperationException("Plugin error identity changed with its message.");
        }
        foreach (var error in new Exception[] { new IOException("只能安装 ZIP 插件包。"),
            new Exception("unknown"), new PluginException((PluginErrorCode)int.MaxValue, "unknown") })
            if (PluginErrors.Key(error) != "PluginInstallFailed")
                throw new InvalidOperationException("Unknown plugin errors must use the generic key.");
        Console.WriteLine("PASS plugin errors use stable codes independently of message text");
    }
}
