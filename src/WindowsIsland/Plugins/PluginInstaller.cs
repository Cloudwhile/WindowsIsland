using System.IO.Compression;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed class PreparedPlugin(string directory, PluginManifest manifest, PluginTrust trust) : IDisposable
{
    public string Directory { get; } = directory;
    public PluginManifest Manifest { get; } = manifest;
    public PluginTrust Trust { get; } = trust;
    public void Dispose() => PluginPaths.DeleteTree(Directory);
}

internal sealed class PluginInstaller(PluginPaths paths)
{
    public Task<PreparedPlugin> PrepareAsync(string zip, PluginSourceKind kind = PluginSourceKind.Manual,
        IEnumerable<string>? confirmed = null, CancellationToken token = default, PluginNetworkDisclosure? sourceNetwork = null)
        => Task.Run(() => Prepare(zip, kind, confirmed, token, sourceNetwork), token);

    private PreparedPlugin Prepare(string zip, PluginSourceKind kind, IEnumerable<string>? confirmed, CancellationToken token,
        PluginNetworkDisclosure? sourceNetwork)
    {
        if (!Path.GetExtension(zip).Equals(".zip", StringComparison.OrdinalIgnoreCase)) throw new PluginException(PluginErrorCode.Zip, "只能安装 ZIP 插件包。");
        var stage = Path.Combine(paths.Slot(kind), ".stage-" + Guid.NewGuid().ToString("N"));
        PluginPaths.CheckLinks(stage);
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            if (archive.Entries.Count is 0 or > 4096 || archive.Entries.Count(e => e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) != 1
                || archive.Entries.Any(e => e.FullName.Contains('/') && Path.GetFileName(e.FullName).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
                throw new PluginException(PluginErrorCode.Manifest, "ZIP 根目录必须且只能包含一个 manifest.json。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            // 完整结构校验先于任何解压写入。
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var name = entry.FullName.TrimEnd('/');
                _ = PluginPaths.Relative(stage, name);
                var mode = (entry.ExternalAttributes >> 16) & 0xF000;
                if (mode is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                    || !names.Add(name) || name.Split('/').Any(IsDevice)) throw new PluginException(PluginErrorCode.Path, "ZIP 含重复路径、特殊文件或符号链接。");
                total = checked(total + entry.Length);
                if (entry.Length > 128L * 1024 * 1024 || total > 256L * 1024 * 1024) throw new PluginException(PluginErrorCode.ExpandedSize, "插件包解压大小超出限制。");
            }
            Directory.CreateDirectory(stage);
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var destination = PluginPaths.Relative(stage, entry.FullName.TrimEnd('/'));
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                var buffer = new byte[65536];
                int count;
                long written = 0;
                while ((count = input.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    written += count;
                    if (written > entry.Length) throw new PluginException(PluginErrorCode.ArchiveLength, "ZIP 文件长度不匹配。");
                    output.Write(buffer, 0, count);
                }
                if (written != entry.Length) throw new PluginException(PluginErrorCode.ArchiveIncomplete, "ZIP 文件不完整。");
            }
            var manifest = PluginManifestReader.Read(stage) with { SourceKind = kind };
            if (manifest.Network is null && sourceNetwork is not null && manifest.Permissions.Contains(PluginPermissions.Network, StringComparer.Ordinal))
            {
                manifest = manifest with { Network = sourceNetwork };
                PluginManifestReader.Validate(manifest);
            }
            PluginAssemblyValidator.Validate(stage, manifest);
            token.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(manifest, PluginManifestReader.Json));
            return new(stage, manifest, PluginTrust.Create(manifest, confirmed));
        }
        catch { PluginPaths.DeleteTree(stage); throw; }
    }

    public static void CheckVersion(PluginManifest incoming, PluginManifest installed, bool overwrite)
    {
        var compared = CatalogVersion.Parse(incoming.Version).CompareTo(CatalogVersion.Parse(installed.Version));
        if (compared < 0) throw new PluginException(PluginErrorCode.Downgrade, "不能安装比当前版本更旧的插件。");
        if (compared == 0 && !overwrite) throw new PluginAlreadyInstalledException();
    }

    private static bool IsDevice(string part)
    {
        var name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" || name.Length == 4 && (name.StartsWith("COM") || name.StartsWith("LPT")) && name[3] is >= '0' and <= '9';
    }
}

internal sealed class PluginAlreadyInstalledException() : IOException("该版本已安装，可确认后覆盖。");
