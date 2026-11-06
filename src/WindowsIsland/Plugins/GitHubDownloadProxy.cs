namespace WindowsIsland.Plugins;

internal static class GitHubDownloadProxy
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new PluginException(PluginErrorCode.ProxyAddress, "GitHub 代理地址无效。");
        return uri.AbsoluteUri;
    }

    public static Uri Target(Uri original, string? proxy)
    {
        var prefix = Normalize(proxy);
        return prefix.Length == 0 ? original : new Uri(prefix + original.AbsoluteUri);
    }

    public static bool AllowsRedirect(Uri next, string? proxy)
    {
        if (next.Scheme != "https" || next.UserInfo.Length != 0) return false;
        if (next.IsDefaultPort && (next.Host == "github.com" || next.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal))) return true;
        var prefix = Normalize(proxy);
        return prefix.Length != 0 && next.GetLeftPart(UriPartial.Authority) == new Uri(prefix).GetLeftPart(UriPartial.Authority);
    }
}
