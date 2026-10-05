using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsIsland.Services;

internal static class UpdateChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var versions = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2",
            "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0" }.Select(Version).ToArray();
        check(versions.Zip(versions.Skip(1)).All(pair => pair.First.CompareTo(pair.Second) < 0),
            "Release comparison follows semantic version precedence including numeric prereleases");
        check(Version("v1.2.3+build.5").CompareTo(Version("1.2.3+build.99")) == 0
            && Version("1.2.3-rc.123456789012345678901").CompareTo(Version("1.2.3-rc.99")) > 0,
            "Build metadata is ignored and long prerelease numbers retain numeric ordering");
        check(new[] { "1.2", "01.2.3", "1.2.3-rc.01", "1.2.3-", "1.2.3 ", "v../../evil", "1.2.3+", "1.2.3-rc..1" }
            .All(value => !ReleaseVersion.TryParse(value, out _)), "Malformed release tags cannot become update candidates");
        check(ReleaseVersion.FromInformationalVersion("0.1.0-rc.1+git123").Prerelease == "rc.1",
            "Current application versions retain prerelease information from assembly metadata");

        var requests = new List<Uri>();
        using (var client = new GitHubReleaseClient(new Handler((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            requests.Add(request.RequestUri!);
            check(request.RequestUri!.AbsoluteUri == GitHubReleaseClient.ManifestUrl
                && request.RequestUri.Scheme == "https" && request.RequestUri.Host == "raw.githubusercontent.com"
                && request.Headers.UserAgent.Count > 0 && request.Headers.Accept.Single().MediaType == "application/json"
                && request.Headers.CacheControl?.NoCache == true
                && !request.Headers.Contains("X-GitHub-Api-Version") && request.Headers.Authorization is null,
                "Update checks use the HTTPS Raw manifest without API headers or login and revalidate cached metadata");
            var entries = new object[] { Release("v1.4.0"), Release("v1.5.0-rc.2", true), Release("v1.3.0"),
                Release("v1.5.0-rc.10", true), Release("v2.0.0", draft: true), Release("v1.6.0", complete: false),
                Release("v1.5.0-rc.99", false), Release("v1.4.1", true), 42, "invalid entry",
                new { tag_name = "v9.0.0", prerelease = "true" } };
            var response = Json(new { schema_version = 1, releases = entries });
            response.Headers.Add("Link", "<https://api.github.com/unused>; rel=\"next\"");
            return Task.FromResult(response);
        })))
        {
            var stable = await client.FindUpdateAsync(Version("1.2.0"), false, CancellationToken.None);
            check(stable?.Tag == "v1.4.0" && requests.Count == 1,
                "Stable updates use one manifest request and exclude drafts, invalid entries and both forms of prerelease");
            requests.Clear();
            var preview = await client.FindUpdateAsync(Version("1.2.0"), true, CancellationToken.None);
            check(preview?.Tag == "v1.5.0-rc.99" && requests.Count == 1 && preview.Notes == "修复与改进"
                && preview.Installer?.Name.EndsWith(".msi", StringComparison.Ordinal) == true,
                "Preview updates retain release notes and MSI metadata and choose the highest version regardless of manifest order");
            check(await client.FindUpdateAsync(Version("1.5.0-rc.99"), true, CancellationToken.None) is null,
                "The current version and older releases are never offered as an update");
        }
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.NotFound })
        {
            using var client = new GitHubReleaseClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
            try { await client.FindUpdateAsync(Version("0.0.0"), false, CancellationToken.None); check(false, "HTTP failures are reported"); }
            catch (HttpRequestException error) { check(error.StatusCode == status, "Raw manifest HTTP failures remain retryable: " + status); }
        }
        foreach (var body in new[] { "null", "[]", "<html>Unavailable</html>", "{}",
            "{\"schema_version\":2,\"releases\":[]}", "{\"schema_version\":\"1\",\"releases\":[]}",
            "{\"schema_version\":1,\"releases\":null}" })
        {
            using var client = new GitHubReleaseClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body) })));
            try { await client.FindUpdateAsync(Version("0.0.0"), true, CancellationToken.None); check(false, "Invalid manifests are reported"); }
            catch (InvalidDataException error) { check(Localization.ErrorKey(error, "") == "UpdateErrorManifest",
                "Malformed or unsupported manifests cannot appear as no available updates: " + body); }
        }
        using (var client = new GitHubReleaseClient(new Handler((_, _) => Task.FromResult(Json(new { schema_version = 1, releases = Array.Empty<object>() })))))
            check(await client.FindUpdateAsync(Version("0.0.0"), true, CancellationToken.None) is null,
                "A valid empty manifest means no available update");
        using (var client = new GitHubReleaseClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[GitHubReleaseClient.MaxManifestBytes + 1]) }))))
        {
            try { await client.FindUpdateAsync(Version("0.0.0"), true, CancellationToken.None); check(false, "Manifest sizes are bounded"); }
            catch (HttpRequestException) { check(true, "Oversized manifests are rejected before JSON parsing"); }
        }
        using (var client = new GitHubReleaseClient(new Handler((_, token) =>
            { token.ThrowIfCancellationRequested(); return Task.FromResult(Json(new { schema_version = 1, releases = Array.Empty<object>() })); })))
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try { await client.FindUpdateAsync(Version("0.0.0"), true, cancel.Token); check(false, "Manifest cancellation is observed"); }
            catch (OperationCanceledException) { check(true, "Manifest checks support cancellation without issuing an API request"); }
        }
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(Release("v1.0.0", unsafeUrl: true))))
            check(GitHubReleaseClient.ParseRelease(document.RootElement) is null, "Release assets outside the project download path are rejected");
        foreach (var invalid in new[] { "wrong-tag", "wrong-size", "duplicate", "invalid-flag" })
        {
            var entry = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Release("v1.0.0")))!;
            var assets = entry["assets"]!.AsArray();
            if (invalid == "wrong-tag") assets[0]!["browser_download_url"] = assets[0]!["browser_download_url"]!.GetValue<string>().Replace("/v1.0.0/", "/v2.0.0/");
            if (invalid == "wrong-size") assets[0]!["size"] = "1024";
            if (invalid == "duplicate") assets.Add(assets[0]!.DeepClone());
            if (invalid == "invalid-flag") entry["prerelease"] = "true";
            using var document = JsonDocument.Parse(entry.ToJsonString());
            check(GitHubReleaseClient.ParseRelease(document.RootElement) is null, "Invalid release metadata is skipped: " + invalid);
        }

        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Environment.CurrentDirectory, "updates", "releases.json"))))
        {
            var published = document.RootElement.GetProperty("releases").EnumerateArray().Select(GitHubReleaseClient.ParseRelease).ToArray();
            check(document.RootElement.GetProperty("schema_version").GetInt32() == 1 && published.Length > 0
                && published.All(release => release is { Installer: not null, InstallerChecksum: not null }),
                "The checked-in manifest of actual published releases is readable by the client with both ZIP and MSI assets");
        }

        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zip = Archive();
        using var metadata = JsonDocument.Parse(JsonSerializer.Serialize(Release("v9.9.9", zip.Length)));
        var release = GitHubReleaseClient.ParseRelease(metadata.RootElement)!;
        var digest = Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant() + "  " + release.Archive.Name + "\n");
        release = release with { Checksum = release.Checksum with { Size = digest.Length } };
        using (var client = DownloadClient(zip, digest))
        {
            string directory;
            using (var prepared = await UpdatePackage.PrepareAsync(client, release, false, null, CancellationToken.None, root))
            {
                directory = prepared.Directory;
                check(UpdatePackage.RequiredFiles.All(file => File.Exists(Path.Combine(prepared.StagingDirectory, file))),
                    "Verified updates extract all required runtime and application resources to an isolated directory");
            }
            check(!Directory.Exists(directory), "Cancelling a prepared update removes only its temporary job directory");
        }
        var wrong = Encoding.UTF8.GetBytes(new string('0', 64) + "  " + release.Archive.Name + "\n");
        using (var client = DownloadClient(zip, wrong))
        {
            await RejectAsync(() => UpdatePackage.PrepareAsync(client, release with { Checksum = release.Checksum with { Size = wrong.Length } },
                false, null, CancellationToken.None, root), check, "Checksum mismatches never reach extraction and remove partial downloads");
            check(!Directory.EnumerateDirectories(root).Any(), "Failed downloads leave no prepared update behind");
        }
        using (var client = DownloadClient(zip[..^1], digest))
            await RejectAsync(() => UpdatePackage.PrepareAsync(client, release, false, null, CancellationToken.None, root), check,
                "Truncated downloads are rejected before replacing the application");
        using (var client = DownloadClient(zip, digest))
            await RejectAsync(() => UpdatePackage.PrepareAsync(client, release with { Installer = null, InstallerChecksum = null }, true,
                null, CancellationToken.None, root), check, "MSI installations require a matching MSI instead of overwriting installer files");
        using (var client = DownloadClient(zip, digest))
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try { await UpdatePackage.PrepareAsync(client, release, false, null, cancel.Token, root); check(false, "Download cancellation is observed"); }
            catch (OperationCanceledException) { check(!Directory.EnumerateDirectories(root).Any(), "Cancelled downloads clean up without changing the installed version"); }
        }
        foreach (var path in new[] { "../outside.txt", "/outside.txt", "folder/../../outside.txt", "C:/outside.txt", "folder/../evil.txt", "folder/file:stream" })
            RejectArchive(root, Archive((path, 0)), check, "ZIP path traversal and rooted paths are rejected: " + path);
        RejectArchive(root, Archive(("links/shortcut", unchecked((int)0xA0000000))), check, "ZIP symbolic links are rejected");
        RejectArchive(root, Archive(("windowsisland.exe", 0)), check, "Case-insensitive ZIP path collisions are rejected");
        RejectArchive(root, Archive(includeRequired: false), check, "Incomplete ZIP packages cannot be staged");
        RejectArchive(root, Archive(omitRequired: "Updater/Apply-Update.ps1"), check,
            "Update packages must retain the helper required for subsequent updates");
        try { UpdatePackage.ReadChecksum(new string('a', 64) + "  wrong.zip", release.Archive.Name); check(false, "Checksum filenames match"); }
        catch (InvalidDataException) { check(true, "Checksum files must identify the selected release asset"); }
    }

    private static ReleaseVersion Version(string value) => ReleaseVersion.TryParse(value, out var version) ? version! : throw new ArgumentException(value);

    private static object Release(string tag, long size = 1024, bool prerelease = false, bool draft = false,
        bool complete = true, bool unsafeUrl = false) => new
    {
        tag_name = tag, name = "Windows Island " + tag, body = "修复与改进", draft, prerelease,
        assets = (complete ? new[] { ".zip", ".zip.sha256", ".msi", ".msi.sha256" } : new[] { ".zip" }).Select(extension => new
        {
            name = "WindowsIsland-" + tag[1..] + "-win-x64" + extension,
            browser_download_url = (unsafeUrl ? "https://example.com/" : GitHubReleaseClient.DownloadPrefix + tag + "/")
                + "WindowsIsland-" + tag[1..] + "-win-x64" + extension,
            size = extension.EndsWith("sha256", StringComparison.Ordinal) ? 100 : size
        }).ToArray()
    };

    private static object Release(string tag, bool prerelease) => Release(tag, 1024, prerelease);
    private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };

    private static GitHubReleaseClient DownloadClient(byte[] archive, byte[] checksum) => new(new Handler((request, token) =>
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? checksum : archive)
        });
    }));

    private static byte[] Archive((string Name, int Attributes)? extra = null, bool includeRequired = true, string? omitRequired = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            if (includeRequired)
                foreach (var file in UpdatePackage.RequiredFiles.Where(file => file != omitRequired))
                    using (var writer = new StreamWriter(archive.CreateEntry(file).Open())) writer.Write("fixture");
            if (extra is { } entry)
            {
                var item = archive.CreateEntry(entry.Name);
                item.ExternalAttributes = entry.Attributes;
                using var writer = new StreamWriter(item.Open());
                writer.Write("fixture");
            }
        }
        return stream.ToArray();
    }

    private static void RejectArchive(string root, byte[] archive, Action<bool, string> check, string name)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(path, archive);
        var destination = Path.Combine(root, Guid.NewGuid().ToString("N"));
        try { UpdatePackage.Extract(path, destination, CancellationToken.None); check(false, name); }
        catch (InvalidDataException) { check(!Directory.Exists(destination), name); }
    }

    private static async Task RejectAsync(Func<Task<PreparedUpdate>> action, Action<bool, string> check, string name)
    {
        try { using var update = await action(); check(false, name); }
        catch (InvalidDataException) { check(true, name); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
