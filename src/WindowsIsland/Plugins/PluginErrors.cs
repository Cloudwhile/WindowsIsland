namespace WindowsIsland.Plugins;

internal static class PluginErrors
{
    public static string Key(Exception error) => error is PluginException plugin ? Key(plugin.Code) : "PluginInstallFailed";
    public static string? DiagnosticKey(int hresult) => (hresult & unchecked((int)0xFFFF0000)) == PluginException.ErrorBase
        && Enum.IsDefined((PluginErrorCode)(hresult & 0xFFFF)) ? Key((PluginErrorCode)(hresult & 0xFFFF)) : null;
    public static string Key(PluginErrorCode code) => code switch
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
        PluginErrorCode.ProxyAddress => "PluginProxyInvalid",
        PluginErrorCode.Hash => "PluginErrorHash",
        PluginErrorCode.IndexMismatch => "PluginErrorIndexMismatch",
        PluginErrorCode.NotListed => "PluginErrorNotListed",
        PluginErrorCode.CatalogFetch => "PluginCatalogFetchFailed",
        PluginErrorCode.CatalogStatus => "PluginCatalogStatusFailed",
        PluginErrorCode.CatalogJson => "PluginCatalogJsonFailed",
        PluginErrorCode.CatalogTimeout => "PluginCatalogTimedOut",
        PluginErrorCode.SourceList or PluginErrorCode.SourceMetadata => "PluginCatalogSourceInvalid",
        PluginErrorCode.IndexSize or PluginErrorCode.IndexDuplicate => "PluginCatalogIndexInvalid",
        PluginErrorCode.IndexMetadata => "PluginCatalogEntryInvalid",
        PluginErrorCode.SourceSize => "PluginCatalogResponseTooLarge",
        PluginErrorCode.CatalogCache or PluginErrorCode.CacheSize => "PluginCatalogCacheFailed",
        _ => "PluginInstallFailed"
    };
}
