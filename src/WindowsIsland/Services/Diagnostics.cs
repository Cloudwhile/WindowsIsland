using System.Runtime.CompilerServices;

namespace WindowsIsland.Services;

internal static class Diagnostics
{
    private static DiagnosticStore? _store;
    public static DiagnosticStore? Store => Volatile.Read(ref _store);

    public static void Initialize(string directory, bool enabled)
    {
        try { Interlocked.Exchange(ref _store, new DiagnosticStore(directory, enabled))?.Dispose(); }
        catch { } // Startup must still work when the local diagnostics directory is unavailable.
    }

    public static string? Capture(DiagnosticArea area, Exception error, [CallerMemberName] string operation = "unknown", bool fatal = false) =>
        Store?.Record(area, operation, error, fatal);
    public static string? State(DiagnosticArea area, string operation, string? clientVersion = null) =>
        Store?.Record(area, operation, clientVersion: clientVersion);
    public static void Shutdown() => Interlocked.Exchange(ref _store, null)?.Dispose();
}
