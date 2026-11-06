using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsIsland.Plugins;

// 持有稳定安装树：已存在的文件不能写入或替换，目录不能被移动。
// 请求只检查树成员与宿主收据，不重新读取包正文。
internal sealed class PluginRunSnapshot : IDisposable
{
    private readonly ExternalPlugin _plugin;
    private readonly List<IDisposable> _handles = [];
    private string[] _members = [];
    private int _disposed;
    private int _invalidated;
    public PluginConsentDescriptor Consent { get; private set; } = null!;

    private PluginRunSnapshot(ExternalPlugin plugin) => _plugin = plugin;

    public static PluginRunSnapshot Open(ExternalPlugin plugin)
    {
        var snapshot = new PluginRunSnapshot(plugin);
        try
        {
            PluginPaths.CheckTree(plugin.Directory);
            var directories = new Stack<string>();
            for (var parent = new DirectoryInfo(plugin.Directory); parent is not null; parent = parent.Parent)
                directories.Push(parent.FullName);
            void LockDirectory(string path)
            {
                var handle = CreateFile(path, 0x80, 3, 0, 3, 0x02200000, 0);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot lock plugin directory."); }
                snapshot._handles.Add(handle);
            }
            foreach (var path in directories) LockDirectory(path);
            snapshot._members = snapshot.Members();
            if (snapshot._members.Length > 8192) throw new InvalidDataException("Plugin tree is too large.");
            foreach (var member in snapshot._members)
            {
                var path = PluginPaths.Relative(plugin.Directory, member);
                if (Directory.Exists(path)) LockDirectory(path);
                else snapshot._handles.Add(PluginFileSafety.OpenSingleLinkRead(path));
            }
            PluginPaths.CheckTree(plugin.Directory);
            snapshot.Consent = PluginConsentPolicy.Describe(plugin);
            if (!snapshot.IsCurrent()) throw new UnauthorizedAccessException();
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    private string[] Members() => Directory.EnumerateFileSystemEntries(_plugin.Directory, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(_plugin.Directory, path).Replace('\\', '/')).Order(StringComparer.Ordinal).Take(8193).ToArray();

    public bool IsCurrent()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _invalidated) != 0) return false;
        try
        {
            PluginPaths.CheckTree(_plugin.Directory);
            if (_members.SequenceEqual(Members(), StringComparer.Ordinal)
                && PluginConsentPolicy.HasConsent(_plugin, Consent) && Volatile.Read(ref _disposed) == 0) return true;
        }
        catch { }
        Interlocked.Exchange(ref _invalidated, 1);
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        _handles.Clear();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
