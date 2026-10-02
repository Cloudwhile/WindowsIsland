using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.Notifications;

namespace WindowsIsland.Services;

internal sealed class NotificationImageReader
{
    private const int MaximumImageBytes = 4194304;
    private readonly Dictionary<string, byte[]> _icons = new(StringComparer.Ordinal);

    public async Task<byte[]?> ReadIconAsync(AppInfo? app, string? appId = null)
    {
        var key = appId;
        try { key ??= app?.AppUserModelId; }
        catch (Exception) { }
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (_icons.TryGetValue(key, out var cached)) return cached;
        byte[]? bytes = null;
        try
        {
            using var stream = await app!.DisplayInfo.GetLogo(new Size(48, 48)).OpenReadAsync();
            if (stream.Size is > 0 and <= MaximumImageBytes)
            {
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                var length = (uint)stream.Size;
                if (await reader.LoadAsync(length) == length)
                {
                    bytes = new byte[length];
                    reader.ReadBytes(bytes);
                }
            }
        }
        catch (Exception) { }
        bytes ??= ShellAppIcon.Read(key);
        if (bytes is not { Length: > 0 }) return null;
        if (_icons.Count >= 64) _icons.Clear();
        _icons[key] = bytes;
        return bytes;
    }

    public async Task<byte[]?> ReadAvatarAsync(IslandNotification notification, AppInfo? app)
    {
        if (string.IsNullOrWhiteSpace(notification.AppId)) return null;
        try
        {
            // The listener exposes text only. Toast history retains the original image references.
            var image = await Task.Run(() => NotificationImageStore.Read(notification));
            if (image is null)
            {
                var candidates = ToastNotificationManager.History.GetHistory(notification.AppId)
                    .Select(item => ToastImageReference.Parse(item.Content.GetXml()))
                    .OfType<ToastImageReference>();
                image = ToastImageReference.Match(notification, candidates);
            }
            if (image is null) return null;
            string? packageFolder = null, applicationDataFolder = null;
            if (image.Avatar is not null && (image.Avatar.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase)
                || image.Avatar.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(app?.PackageFamilyName))
            {
                packageFolder = new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty, app.PackageFamilyName)
                    .FirstOrDefault()?.InstalledLocation.Path;
                applicationDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", app.PackageFamilyName);
            }
            var path = image.LocalPath(packageFolder, applicationDataFolder);
            if (path is null) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumImageBytes) return null;
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes);
            return bytes;
        }
        catch (Exception) { return null; }
    }

    public void Clear() => _icons.Clear();
}
