using System.Xml;
using System.Xml.Linq;

namespace WindowsIsland.Services;

internal sealed record ToastImageReference(string Title, string Body, string? Avatar, string? BaseUri)
{
    public static ToastImageReference? Parse(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 131072
            });
            var document = XDocument.Load(reader);
            var bindings = document.Descendants("binding").ToArray();
            var binding = bindings.FirstOrDefault(item => (string?)item.Attribute("template") == "ToastGeneric")
                ?? bindings.FirstOrDefault();
            if (binding is null) return null;
            var text = binding.Descendants("text").Select(item => item.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (text.Length == 0) return null;
            var images = binding.Elements("image");
            var image = images.FirstOrDefault(item => (string?)item.Attribute("placement") == "appLogoOverride");
            if (image is null && ((string?)binding.Attribute("template"))?.StartsWith("ToastImageAndText", StringComparison.Ordinal) == true)
                image = images.FirstOrDefault();
            var baseUri = (string?)binding.Attribute("baseUri") ?? (string?)binding.Parent?.Attribute("baseUri");
            return new(text[0], string.Join("\n", text.Skip(1)), (string?)image?.Attribute("src"), baseUri);
        }
        catch (XmlException) { return null; }
    }

    public static ToastImageReference? Match(IslandNotification notification, IEnumerable<ToastImageReference> candidates)
    {
        var matches = candidates.Where(item => Normalize(item.Title) == Normalize(notification.Title)
            && Normalize(item.Body) == Normalize(notification.Body)).ToArray();
        if (matches.Length == 0) return null;
        // Repeated messages can have identical text; an ambiguous photo must never identify another sender.
        return matches.All(item => item.Avatar == matches[0].Avatar && item.BaseUri == matches[0].BaseUri)
            ? matches[0] : null;
    }

    public string? LocalPath(string? packageFolder = null, string? applicationDataFolder = null)
    {
        if (string.IsNullOrWhiteSpace(Avatar)) return null;
        try
        {
            if (Path.IsPathFullyQualified(Avatar) && !Avatar.StartsWith("\\\\", StringComparison.Ordinal))
                return Path.GetFullPath(Avatar);
            if (!Uri.TryCreate(Avatar, UriKind.Absolute, out var uri))
            {
                if (!Uri.TryCreate(BaseUri, UriKind.Absolute, out var root) || !Uri.TryCreate(root, Avatar, out uri)) return null;
            }
            if (uri.IsFile) return uri.IsUnc ? null : uri.LocalPath;
            if (uri.Scheme == "ms-appx") return UnderFolder(packageFolder, uri.AbsolutePath);
            if (uri.Scheme == "ms-appdata")
            {
                var parts = uri.AbsolutePath.TrimStart('/').Split('/', 2);
                var directory = parts[0] switch { "local" => "LocalState", "roaming" => "RoamingState", "temp" => "TempState", _ => null };
                return directory is not null && parts.Length == 2 && applicationDataFolder is not null
                    ? UnderFolder(Path.Combine(applicationDataFolder, directory), parts[1]) : null;
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or UriFormatException) { }
        return null;
    }

    private static string? UnderFolder(string? folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(relative).TrimStart('/', '\\')));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static string Normalize(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
}
