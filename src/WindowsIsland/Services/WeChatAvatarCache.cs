using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WindowsIsland.Services;

internal sealed class WeChatAvatarCache(string? directory = null, string scope = "")
{
    internal sealed record Entry(byte[] Bytes, DateTimeOffset ReadAt);
    private readonly Dictionary<(string Key, string Conversation), Entry?> _entries = [];
    private bool _pruned;

    public Entry? Get((string Key, string Conversation) key)
    {
        if (_entries.TryGetValue(key, out var entry)) return entry;
        try
        {
            var path = FilePath(key);
            if (path is not null && new FileInfo(path) is { Exists: true, Length: > 0 and <= 65536 } file)
            {
                var bytes = File.ReadAllBytes(path);
                using var stream = new MemoryStream(bytes);
                using var image = Image.FromStream(stream, false, true);
                if (image.Width == 64 && image.Height == 64) entry = new(bytes, file.LastWriteTimeUtc);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { }
        Remember(key, entry);
        return entry;
    }

    public void Store((string Key, string Conversation) key, byte[] bytes)
    {
        var unchanged = _entries.TryGetValue(key, out var previous) && previous is not null
            && previous.Bytes.AsSpan().SequenceEqual(bytes);
        Remember(key, new(bytes, DateTimeOffset.UtcNow));
        if (directory is null) return;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(directory);
            if (!_pruned)
            {
                _pruned = true;
                var files = new DirectoryInfo(directory).EnumerateFiles("*.png").OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
                for (var index = 0; index < files.Length; index++)
                    if (index >= 384 || DateTime.UtcNow - files[index].LastWriteTimeUtc > TimeSpan.FromDays(7)) files[index].Delete();
            }
            var path = FilePath(key)!;
            if (unchanged && File.Exists(path)) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return; }
            temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Remember((string Key, string Conversation) key, Entry? entry)
    {
        if (_entries.Count >= 128 && !_entries.ContainsKey(key)) _entries.Remove(_entries.Keys.First());
        _entries[key] = entry;
    }

    private string? FilePath((string Key, string Conversation) key) => directory is null ? null
        : Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{scope.Length}:{scope}{key.Key.Length}:{key.Key}{key.Conversation.Length}:{key.Conversation}"))) + ".png");

    public void Clear() => _entries.Clear();
}
