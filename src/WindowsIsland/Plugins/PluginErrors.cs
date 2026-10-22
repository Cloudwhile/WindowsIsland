namespace WindowsIsland.Plugins;

internal static class PluginErrors
{
    public static string Key(Exception error) => (error as PluginException)?.Code switch
    {
        PluginErrorCode.Zip => "PluginErrorZip",
        PluginErrorCode.Manifest => "PluginErrorManifest",
        PluginErrorCode.Id => "PluginErrorId",
        PluginErrorCode.Contract => "PluginErrorContract",
        PluginErrorCode.Entry => "PluginErrorEntry",
        PluginErrorCode.Downgrade => "PluginErrorDowngrade",
        PluginErrorCode.Version => "PluginErrorVersion",
        PluginErrorCode.Path => "PluginErrorPath",
        PluginErrorCode.SourceConflict => "PluginErrorSourceConflict",
        PluginErrorCode.Start => "PluginErrorStart",
        PluginErrorCode.SourceAddress => "PluginErrorSourceAddress",
        PluginErrorCode.Hash => "PluginErrorHash",
        PluginErrorCode.IndexMismatch => "PluginErrorIndexMismatch",
        PluginErrorCode.NotListed => "PluginErrorNotListed",
        _ => "PluginInstallFailed"
    };
}
