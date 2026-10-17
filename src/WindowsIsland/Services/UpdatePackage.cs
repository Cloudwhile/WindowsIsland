using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace WindowsIsland.Services;

internal sealed record UpdateProgress(string MessageKey, double? Percent = null, double? BytesPerSecond = null);

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
        "en-US/WindowsIsland.resources.dll",
        "WindowsIsland.TelegramHook.dll", "Assets/Island.ico", "Icons/LOGO.png", "Initialization/AppxManifest.xml",
        "Initialization/RuntimeManifest.xml", "Licenses/MinHook.txt", "Updater/Apply-Update.ps1", "Assets/Shell/AppList.png",
        "Assets/Shell/MediumTile.png", "Assets/Shell/StoreLogo.png",
        "Assets/Shell/AppList.targetsize-24_altform-unplated.png", "Assets/Shell/AppList.targetsize-24_altform-lightunplated.png"
    ];

    public static async Task<PreparedUpdate> PrepareAsync(GitHubReleaseClient client, GitHubRelease release,
        bool useInstaller, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken, string? workRoot = null)
    {
        if (useInstaller && (release.Installer is null || release.InstallerChecksum is null
            || !UpdateInstaller.IsInstallerFileName(release.Installer.Name)))
            throw Localization.DataError("UpdateErrorInstallerMissing");
        var root = Path.GetFullPath(workRoot ?? WorkRoot);
        Directory.CreateDirectory(root);
        root = UpdateWorkDirectory.Resolve(root);
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var staging = Path.Combine(directory, "payload");
            string? installer = null;
            if (useInstaller)
            {
                // Keep the release asset name so Windows Installer can identify the
                // package consistently during later repair or maintenance.
                installer = Path.Combine(directory, release.Installer!.Name);
                await DownloadVerifiedAsync(client, release.Installer!, release.InstallerChecksum!, installer, root,
                    progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var archive = Path.Combine(directory, "update.zip");
                await DownloadVerifiedAsync(client, release.Archive, release.Checksum, archive, root,
                    progress, cancellationToken).ConfigureAwait(false);
                progress?.Report(new("UpdateExtracting"));
                await Task.Run(() => Extract(archive, staging, cancellationToken), cancellationToken).ConfigureAwait(false);
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
        string destination, string root, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        var checksumPath = destination + ".sha256";
        await DownloadFreshAsync(client, checksum, checksumPath, 4096, cancellationToken).ConfigureAwait(false);
        var expected = ReadChecksum(await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false), asset.Name);
        var partial = ResumePath(root, expected, asset.Size);
        CleanupResumeFiles(root);
        try
        {
            await DownloadResumableAsync(client, asset, partial, MaximumDownload, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("UpdateVerifying"));
            await using (var file = File.OpenRead(partial))
            {
                var actual = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                    throw Localization.DataError("UpdateErrorChecksumMismatch");
            }
            File.Move(partial, destination);
        }
        catch (InvalidDataException error) when (Localization.ErrorKey(error, "") is "UpdateErrorChecksumMismatch" or "UpdateErrorSizeMismatch")
        {
            DeleteResumeFile(partial);
            throw;
        }
    }

    internal static byte[] ReadChecksum(string text, string filename)
    {
        var parts = text.Trim().Trim((char)0xFEFF).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(char.IsAsciiHexDigit)
            || parts[1].TrimStart('*') != filename)
            throw Localization.DataError("UpdateErrorChecksumInvalid");
        return Convert.FromHexString(parts[0]);
    }

    private static async Task DownloadFreshAsync(GitHubReleaseClient client, ReleaseAsset asset, string destination,
        long limit, CancellationToken cancellationToken)
    {
        if (asset.Size <= 0 || asset.Size > limit) throw Localization.DataError("UpdateErrorSizeInvalid");
        using var response = await client.DownloadAsync(asset.Download, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Localization.DataError("UpdateErrorIncomplete");
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            throw Localization.DataError("UpdateErrorIncomplete");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous);
        var buffer = new byte[128 * 1024];
        long received = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            received += count;
            if (received > asset.Size) throw Localization.DataError("UpdateErrorSizeMismatch");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        if (received != asset.Size) throw Localization.DataError("UpdateErrorIncomplete");
    }

    private static async Task DownloadResumableAsync(GitHubReleaseClient client, ReleaseAsset asset, string partial,
        long limit, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        if (asset.Size <= 0 || asset.Size > limit) throw Localization.DataError("UpdateErrorSizeInvalid");
        if (Directory.Exists(partial)) throw Localization.IoError("UpdateErrorWorkDir");
        if (File.Exists(partial) && (File.GetAttributes(partial) & FileAttributes.ReparsePoint) != 0)
            throw Localization.IoError("UpdateErrorWorkDir");

        await using var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            128 * 1024, FileOptions.Asynchronous);
        if (output.Length > asset.Size) output.SetLength(0);
        var received = output.Length;
        if (received == asset.Size)
        {
            progress?.Report(new("UpdateDownloadComplete", 100));
            return;
        }

        var resumed = received > 0;
        progress?.Report(new(resumed ? "UpdateResuming" : "UpdateDownloading", received * 100d / asset.Size));
        var response = await client.DownloadAsync(asset.Download, cancellationToken, resumed ? received : null).ConfigureAwait(false);
        if (resumed && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            response.Dispose();
            output.SetLength(0);
            received = 0;
            resumed = false;
            progress?.Report(new("UpdateDownloading", 0));
            response = await client.DownloadAsync(asset.Download, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            if (resumed && response.StatusCode == HttpStatusCode.OK)
            {
                output.SetLength(0);
                received = 0;
                resumed = false;
                progress?.Report(new("UpdateDownloading", 0));
            }
            else if (resumed)
            {
                ValidatePartialResponse(response, received, asset.Size);
            }
            else if (response.StatusCode != HttpStatusCode.OK)
            {
                throw Localization.DataError("UpdateErrorIncomplete");
            }

            var expectedResponseLength = asset.Size - received;
            if (response.Content.Headers.ContentLength is { } length && length != expectedResponseLength)
                throw Localization.DataError("UpdateErrorIncomplete");
            output.Position = received;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[128 * 1024];
            var report = Stopwatch.StartNew();
            var total = Stopwatch.StartNew();
            long intervalBytes = 0;
            long transferred = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (received + count > asset.Size) throw Localization.DataError("UpdateErrorSizeMismatch");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                received += count;
                transferred += count;
                intervalBytes += count;
                if (report.ElapsedMilliseconds >= 250)
                {
                    var speed = intervalBytes / report.Elapsed.TotalSeconds;
                    progress?.Report(new(resumed ? "UpdateResuming" : "UpdateDownloading",
                        received * 100d / asset.Size, speed));
                    intervalBytes = 0;
                    report.Restart();
                }
            }
            if (received != asset.Size) throw Localization.DataError("UpdateErrorIncomplete");
            var finalSpeed = transferred > 0 ? transferred / Math.Max(total.Elapsed.TotalSeconds, 0.001) : (double?)null;
            progress?.Report(new("UpdateDownloadComplete", 100, finalSpeed));
        }
    }

    private static void ValidatePartialResponse(HttpResponseMessage response, long start, long total)
    {
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range is null || range.Unit != "bytes"
            || range.From != start || range.To != total - 1 || range.Length != total
            || response.Content.Headers.ContentLength is { } length && length != range.To - range.From + 1)
            throw Localization.DataError("UpdateErrorIncomplete");
    }

    private static string ResumePath(string root, byte[] checksum, long size) => Path.Combine(root,
        "resume-" + Convert.ToHexString(checksum).ToLowerInvariant() + "-" + size.ToString(CultureInfo.InvariantCulture) + ".part");

    private static void CleanupResumeFiles(string root)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "resume-*.part", SearchOption.TopDirectoryOnly))
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    DeleteResumeFile(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.WriteLine(error); }
    }

    private static void DeleteResumeFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.WriteLine(error); }
    }

    internal static void Extract(string archivePath, string destination, CancellationToken cancellationToken)
    {
        if (Directory.Exists(destination)) throw Localization.IoError("UpdateErrorDirectoryExists");
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = new List<(ZipArchiveEntry Entry, string Path)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        if (archive.Entries.Count > 10000) throw Localization.DataError("UpdateErrorTooManyFiles");
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/');
            var parts = relative.TrimEnd('/').Split('/');
            if (relative.Length == 0 || relative.StartsWith('/') || parts.Any(part => part.Length == 0 || part is "." or ".."
                || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw Localization.DataError("UpdateErrorInvalidPath");
            var path = Path.GetFullPath(Path.Combine(destination, Path.Combine(parts)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !paths.Add(path))
                throw Localization.DataError("UpdateErrorConflictingPath");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpanded) throw Localization.DataError("UpdateErrorTooLarge");
            entries.Add((entry, path));
        }
        foreach (var required in RequiredFiles)
        {
            if (!entries.Any(item => string.Equals(item.Entry.FullName.Replace('\\', '/'), required,
                StringComparison.OrdinalIgnoreCase) && item.Entry.Length > 0))
                throw Localization.DataError("UpdateErrorRequiredFile");
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
            throw Localization.IoError("UpdateErrorWorkDir");
        try
        {
            if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(full, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.WriteLine(error); }
    }
}
