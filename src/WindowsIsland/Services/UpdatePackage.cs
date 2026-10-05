using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace WindowsIsland.Services;

internal sealed record UpdateProgress(string Message, double? Percent = null);

internal sealed class PreparedUpdate(string directory, string stagingDirectory, string? installerPath,
    string version, string root) : IDisposable
{
    private bool _handedOff;
    public string Directory { get; } = directory;
    public string StagingDirectory { get; } = stagingDirectory;
    public string? InstallerPath { get; } = installerPath;
    public string Version { get; } = version;
    public void HandOff() => _handedOff = true;
    public void Dispose()
    {
        if (!_handedOff) UpdatePackage.DeleteWorkDirectory(Directory, root);
    }
}

internal static class UpdatePackage
{
    private const long MaximumDownload = 2L * 1024 * 1024 * 1024;
    private const long MaximumExpanded = 4L * 1024 * 1024 * 1024;
    public static string WorkRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsIsland", "Updates");
    internal static readonly string[] RequiredFiles =
    [
        "WindowsIsland.exe", "WindowsIsland.dll", "WindowsIsland.deps.json", "WindowsIsland.runtimeconfig.json",
        "WindowsIsland.pri", "App.xbf", "Microsoft.UI.Xaml.dll", "coreclr.dll", "hostfxr.dll",
        "WindowsIsland.TelegramHook.dll", "Assets/Island.ico", "Icons/LOGO.png", "Initialization/AppxManifest.xml",
        "Initialization/RuntimeManifest.xml", "Licenses/MinHook.txt", "Updater/Apply-Update.ps1", "Assets/Shell/AppList.png",
        "Assets/Shell/MediumTile.png", "Assets/Shell/StoreLogo.png",
        "Assets/Shell/AppList.targetsize-24_altform-unplated.png", "Assets/Shell/AppList.targetsize-24_altform-lightunplated.png"
    ];

    public static async Task<PreparedUpdate> PrepareAsync(GitHubReleaseClient client, GitHubRelease release,
        bool useInstaller, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken, string? workRoot = null)
    {
        if (useInstaller && (release.Installer is null || release.InstallerChecksum is null))
            throw new InvalidDataException("此版本的安装包尚未就绪，请稍后重试。");
        var root = Path.GetFullPath(workRoot ?? WorkRoot);
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var archive = Path.Combine(directory, "update.zip");
            await DownloadVerifiedAsync(client, release.Archive, release.Checksum, archive, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("正在解压更新"));
            var staging = Path.Combine(directory, "payload");
            await Task.Run(() => Extract(archive, staging, cancellationToken), cancellationToken).ConfigureAwait(false);
            string? installer = null;
            if (useInstaller)
            {
                installer = Path.Combine(directory, "update.msi");
                await DownloadVerifiedAsync(client, release.Installer!, release.InstallerChecksum!, installer, progress, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(directory, staging, installer, release.Version.Text, root);
        }
        catch
        {
            DeleteWorkDirectory(directory, root);
            throw;
        }
    }

    private static async Task DownloadVerifiedAsync(GitHubReleaseClient client, ReleaseAsset asset, ReleaseAsset checksum,
        string destination, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        var checksumPath = destination + ".sha256";
        await DownloadAsync(client, checksum, checksumPath, 4096, null, cancellationToken).ConfigureAwait(false);
        var expected = ReadChecksum(await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false), asset.Name);
        await DownloadAsync(client, asset, destination, MaximumDownload, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new("正在校验更新"));
        await using var file = File.OpenRead(destination);
        var actual = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("更新包校验失败，请重新下载。");
    }

    internal static byte[] ReadChecksum(string text, string filename)
    {
        var parts = text.Trim().Trim((char)0xFEFF).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(char.IsAsciiHexDigit)
            || parts[1].TrimStart('*') != filename)
            throw new InvalidDataException("更新校验文件无效，请稍后重试。");
        return Convert.FromHexString(parts[0]);
    }

    private static async Task DownloadAsync(GitHubReleaseClient client, ReleaseAsset asset, string destination, long limit,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        if (asset.Size <= 0 || asset.Size > limit) throw new InvalidDataException("更新文件大小无效，请稍后重试。");
        using var response = await client.DownloadAsync(asset.Download, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            throw new InvalidDataException("更新文件不完整，请重新下载。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous);
        var buffer = new byte[128 * 1024];
        long received = 0;
        var report = Stopwatch.StartNew();
        progress?.Report(new("正在下载更新", 0));
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            received += count;
            if (received > asset.Size) throw new InvalidDataException("更新文件大小不匹配，请重新下载。");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (report.ElapsedMilliseconds >= 100)
            {
                progress?.Report(new("正在下载更新", received * 100d / asset.Size));
                report.Restart();
            }
        }
        if (received != asset.Size) throw new InvalidDataException("更新文件不完整，请重新下载。");
        progress?.Report(new("下载完成", 100));
    }

    internal static void Extract(string archivePath, string destination, CancellationToken cancellationToken)
    {
        if (Directory.Exists(destination)) throw new IOException("更新解压目录已存在。");
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = new List<(ZipArchiveEntry Entry, string Path)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        if (archive.Entries.Count > 10000) throw new InvalidDataException("更新包包含过多文件。");
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/');
            var parts = relative.TrimEnd('/').Split('/');
            if (relative.Length == 0 || relative.StartsWith('/') || parts.Any(part => part.Length == 0 || part is "." or ".."
                || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新包包含无效路径。");
            var path = Path.GetFullPath(Path.Combine(destination, Path.Combine(parts)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !paths.Add(path))
                throw new InvalidDataException("更新包包含冲突路径。");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpanded) throw new InvalidDataException("更新包解压大小超出限制。");
            entries.Add((entry, path));
        }
        foreach (var required in RequiredFiles)
        {
            if (!entries.Any(item => string.Equals(item.Entry.FullName.Replace('\\', '/'), required,
                StringComparison.OrdinalIgnoreCase) && item.Entry.Length > 0))
                throw new InvalidDataException("更新包缺少必要文件，请稍后重试。");
        }
        Directory.CreateDirectory(destination);
        foreach (var (entry, path) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path);
            }
        }
    }

    internal static void DeleteWorkDirectory(string directory, string root)
    {
        var full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
            throw new IOException("更新临时目录无效。");
        try
        {
            if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(full, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.WriteLine(error); }
    }
}
