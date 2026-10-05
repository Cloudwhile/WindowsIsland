using System.Globalization;
using System.IO;
using System.Resources;

namespace WindowsIsland.Services;

internal static class LanguagePreferences
{
    public const string Default = "zh-CN";
    public const string System = "system";
    public static string Normalize(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "system" => System,
        "en-us" => "en-US",
        _ => Default
    };

    public static string Resolve(string? preference, IEnumerable<string>? systemLanguages = null)
    {
        var language = Normalize(preference);
        if (language != System) return language;
        foreach (var candidate in systemLanguages ?? [CultureInfo.CurrentUICulture.Name])
        {
            var primary = candidate.Split('-')[0];
            if (primary.Equals("zh", StringComparison.OrdinalIgnoreCase)) return Default;
            if (primary.Equals("en", StringComparison.OrdinalIgnoreCase)) return "en-US";
        }
        return Default;
    }
}

internal static class Localization
{
    private const string ErrorResource = "WindowsIsland.ResourceKey";
    private static readonly ResourceManager Resources = new(
        typeof(Localization).Assembly.GetName().Name + ".Strings.Resources", typeof(Localization).Assembly);
    private static CultureInfo _culture = CultureInfo.GetCultureInfo(LanguagePreferences.Default);
    public static CultureInfo Culture => Volatile.Read(ref _culture);
    public static string Language => Culture.Name;
    public static event Action? Changed;

    public static void Configure(string? preference, IEnumerable<string>? systemLanguages = null)
    {
        var culture = CultureInfo.GetCultureInfo(LanguagePreferences.Resolve(preference, systemLanguages));
        if (Interlocked.Exchange(ref _culture, culture).Name != culture.Name) Changed?.Invoke();
    }

    public static string Get(string key) => Resources.GetString(key, Culture)
        ?? throw new MissingManifestResourceException("Missing translation: " + key);
    public static string Format(string key, params object[] arguments) => string.Format(Culture, Get(key), arguments);
    public static string ErrorKey(Exception error, string fallback) => error.Data[ErrorResource] as string ?? fallback;
    public static T Error<T>(T error, string key) where T : Exception
    {
        error.Data[ErrorResource] = key;
        return error;
    }
    public static InvalidDataException DataError(string key) => Error(new InvalidDataException(Get(key)), key);
    public static IOException IoError(string key) => Error(new IOException(Get(key)), key);
}
