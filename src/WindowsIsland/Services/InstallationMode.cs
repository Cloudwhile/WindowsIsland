namespace WindowsIsland.Services;

internal static class InstallationMode
{
    internal const string PortableMarker = "portable.marker";
    public static bool IsPortable(string? directory = null) =>
        File.Exists(Path.Combine(directory ?? AppContext.BaseDirectory, PortableMarker));
}
