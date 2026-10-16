using System.Security.Cryptography;

namespace WindowsIsland.Services;

internal static class InitializationResources
{
    public static void Prepare(string source, string destination)
    {
        if (Matches(source, destination)) return;
        // WinUI may already map an identical resources.pri after a failed first
        // registration. Do not try to overwrite that mapped file on retry.
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool Matches(string source, string destination)
    {
        if (!File.Exists(destination)) return false;
        using var original = File.OpenRead(source);
        using var existing = File.OpenRead(destination);
        return original.Length == existing.Length && SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(existing));
    }
}
