using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal static partial class PluginCatalogPolicy
{
    public const int MaximumResponseBytes = 4 * 1024 * 1024;
    public static string BaseAddress(string? value)
    {
        if (value is null || value != value.Trim() || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || value.Contains('?') || value.Contains('#') || value.Contains('\\') || AuthorityHasUserInfo(value)
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new PluginException(PluginErrorCode.SourceAddress, "目录基地址无效。", "C2");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }
    public static bool Identifier(string? value, int limit) => value is { Length: > 0 } && value.Length <= limit && IdentifierPattern().IsMatch(value);
    public static bool Repository(string? value) => value is not null && RepositoryPattern().IsMatch(value)
        && !value.EndsWith(".git", StringComparison.OrdinalIgnoreCase);
    public static void Source(PluginSource? source, Uri baseUri)
    {
        if (source is null || !Identifier(source.DeveloperId, 64)) Fail(PluginErrorCode.SourceMetadata, "C13");
        if (string.IsNullOrWhiteSpace(source!.DeveloperName) || source.DeveloperName.Length > 200 || !Repository(source.RepositoryUrl))
            Fail(PluginErrorCode.SourceMetadata, "C14");
        if (source.IndexUrl != baseUri.AbsoluteUri + "api/sources/" + source.DeveloperId + "/plugins") Fail(PluginErrorCode.SourceMetadata, "C15");
    }
    public static void Entry(PluginCatalogEntry? entry, PluginSource source)
    {
        if (entry is null || !PluginId.IsValid(entry.Id) || entry.Id.Length > 80 || PluginManifestReader.Reserved(entry.Id)) Fail(PluginErrorCode.IndexMetadata, "C17");
        if (entry!.DeveloperId != source.DeveloperId || entry.RepositoryUrl != source.RepositoryUrl) Fail(PluginErrorCode.IndexMetadata, "C18");
        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 200 || entry.Description is null || entry.Description.Length > 8000) Fail(PluginErrorCode.IndexMetadata, "C19");
        if (!CatalogVersion.TryParse(entry.Version, out _) || !CatalogVersion.TryParse(entry.MinHostVersion, out _)) Fail(PluginErrorCode.IndexMetadata, "C20");
        if (entry.ContractVersion < 1) Fail(PluginErrorCode.IndexMetadata, "C21");
        if (entry.Permissions is null || entry.Permissions.Length > 64 || entry.Permissions.Any(p => !Identifier(p, 80) || !PluginPermissions.IsKnown(p))
            || entry.Permissions.Distinct(StringComparer.Ordinal).Count() != entry.Permissions.Length) Fail(PluginErrorCode.IndexMetadata, "C22");
        if (entry.Sha256 is not { Length: 64 } || !entry.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) Fail(PluginErrorCode.IndexMetadata, "C23");
        if (!Download(entry.DownloadUrl, source.RepositoryUrl)) Fail(PluginErrorCode.IndexMetadata, "C24");
    }
    private static bool Download(string? value, string repository)
    {
        if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || value.Contains('?') || value.Contains('#') || value.Contains('\\') || AuthorityHasUserInfo(value)) return false;
        // 校验原始路径，避免 Uri 提前折叠点段；逐层解码，拒绝编码或双重编码的分隔符。
        var prefix = repository["https://github.com".Length..] + "/releases/download/";
        var authorityEnd = value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3);
        if (authorityEnd < 0) return false;
        var path = value[authorityEnd..];
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var segments = path[prefix.Length..].Split('/');
        if (segments.Length != 2 || segments.Any(s => s.Length == 0) || !segments[1].EndsWith(".zip", StringComparison.Ordinal)) return false;
        foreach (var segment in segments)
        {
            var decoded = segment;
            for (var pass = 0; pass <= segment.Length; pass++)
            {
                if (decoded.Contains("..", StringComparison.Ordinal) || decoded.Contains('/') || decoded.Contains('\\')) return false;
                var next = Uri.UnescapeDataString(decoded);
                if (next == decoded) break;
                decoded = next;
            }
        }
        return true;
    }
    public static T[] Array<T>(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Array) Fail(PluginErrorCode.CatalogJson, "C7");
            UniqueKeys(json.RootElement);
            return json.RootElement.Deserialize<T[]>(new JsonSerializerOptions
                { PropertyNameCaseInsensitive = false, Converters = { new ContractIntegerConverter() } })
                ?? throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or NotSupportedException)
        { throw new PluginException(PluginErrorCode.CatalogJson, "目录响应格式无效。", "C7"); }
    }
    private static void UniqueKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!keys.Add(property.Name)) Fail(PluginErrorCode.CatalogJson, "C7");
                UniqueKeys(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) UniqueKeys(item);
    }
    public static void Directory(IEnumerable<PluginSourceIndex> indices)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in indices)
            foreach (var entry in index.Plugins)
            {
                if (!ids.Add(entry.Id)) Fail(PluginErrorCode.IndexDuplicate, "C17");
                if (ids.Count > 8192) Fail(PluginErrorCode.IndexSize, "C16");
            }
    }
    private sealed class ContractIntegerConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            // Contracts exposes Int32. Larger positive JSON integers are valid but necessarily incompatible;
            // represent them by an incompatible major instead of rejecting a legal directory response.
            using var number = JsonDocument.ParseValue(ref reader);
            var text = number.RootElement.GetRawText();
            if (number.RootElement.ValueKind != JsonValueKind.Number || !text.All(char.IsAsciiDigit))
                Fail(PluginErrorCode.IndexMetadata, "C21");
            return int.TryParse(text, out var value) ? value : int.MaxValue;
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }
    private static bool AuthorityHasUserInfo(string value)
    {
        var start = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = value.IndexOf('/', start);
        return value.AsSpan(start, (end < 0 ? value.Length : end) - start).Contains('@');
    }
    private static void Fail(PluginErrorCode code, string rule) => throw new PluginException(code, "目录校验失败。", rule);
    [GeneratedRegex(@"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant)] private static partial Regex IdentifierPattern();
    [GeneratedRegex(@"\Ahttps://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant)] private static partial Regex RepositoryPattern();
}
