using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;

namespace WindowsIsland.Plugins;

[SupportedOSPlatform("windows")]
internal static class SandboxAccess
{
    private static readonly object HostDataRootGate = new();
    private static readonly string[] HostPluginAreas = ["host-consents", "host-settings", "host-installations", "host-provenance"];
    private static string? _requiredHostDataRoot;
    private static bool _hostDataRootProtected;
    private static bool _externalPluginsDisabled;
    private static Exception? _hostDataRootError;

    internal static bool IsHostDataRootBlocked
    {
        get { lock (HostDataRootGate) return _externalPluginsDisabled || (_requiredHostDataRoot is not null && !_hostDataRootProtected); }
    }

    internal static Exception? HostDataRootError
    {
        get { lock (HostDataRootGate) return _hostDataRootError; }
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static void DisableExternalPlugins()
    {
        lock (HostDataRootGate)
        {
            _externalPluginsDisabled = true;
            _requiredHostDataRoot = null;
            _hostDataRootProtected = false;
            _hostDataRootError = null;
        }
    }

    public static void RequireHostDataRootProtection(string root)
    {
        var fullRoot = NormalizePath(root);
        lock (HostDataRootGate)
        {
            _externalPluginsDisabled = false;
            _requiredHostDataRoot = fullRoot;
            _hostDataRootProtected = false;
            _hostDataRootError = null;
        }
    }

    public static void RecordHostDataRootFailure(Exception error)
    {
        lock (HostDataRootGate)
        {
            _hostDataRootProtected = false;
            _hostDataRootError = error;
        }
    }

    internal static void ResetHostDataRootProtection()
    {
        lock (HostDataRootGate)
        {
            _externalPluginsDisabled = false;
            _requiredHostDataRoot = null;
            _hostDataRootProtected = false;
            _hostDataRootError = null;
        }
    }

    public static void EnsureHostDataRootProtected()
    {
        lock (HostDataRootGate)
        {
            if (_externalPluginsDisabled)
                throw new InvalidOperationException("External plugins are disabled in verification mode.");
            if (_requiredHostDataRoot is null || _hostDataRootProtected) return;
            try
            {
                ProtectHostDataRootCore(_requiredHostDataRoot);
                _hostDataRootProtected = true;
                _hostDataRootError = null;
            }
            catch (Exception error)
            {
                _hostDataRootProtected = false;
                _hostDataRootError = error;
                throw new InvalidOperationException("Host data root protection failed; external plugins cannot start.", error);
            }
        }
    }

    public static void ProtectHostDirectory(string directory)
    {
        PluginPaths.CheckLinks(directory);
        Directory.CreateDirectory(directory);
        ApplyDirectory(directory, WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException(), null, false, propagate: false);
    }

    public static void ProtectHostFile(string file)
    {
        PluginPaths.CheckLinks(file);
        PluginFileSafety.CheckSingleLink(file);
        ApplyFile(file, WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException(), null, false);
    }

    // Resolve Desktop Bridge virtualized AppData paths to their physical NTFS directory path
    // so child LPAC workers (which do not inherit Desktop Bridge VFS redirection) can load
    // hostfxr/runtime, plugin code, and writable data directly.
    public static string PhysicalDirectory(string directory)
    {
        PluginPaths.CheckLinks(directory);
        using var handle = SandboxNative.CreateFile(directory, 0x80u, 7u, 0, 3u, 0x02200000u, 0);
        if (handle.IsInvalid) throw SecurityError(Marshal.GetLastWin32Error());
        if (!SandboxNative.GetFileInformationByHandle(handle, out var info)) throw SecurityError(Marshal.GetLastWin32Error());
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new PluginException(PluginErrorCode.Path, "插件目录不能包含符号链接或重解析点。");
        if ((info.Attributes & (uint)FileAttributes.Directory) == 0)
            throw new IOException("Expected a directory.");
        var capacity = 260u;
        var buffer = new StringBuilder((int)capacity);
        var length = SandboxNative.GetFinalPathNameByHandle(handle, buffer, capacity, 0);
        if (length == 0) throw SecurityError(Marshal.GetLastWin32Error());
        if (length >= capacity)
        {
            capacity = length + 1;
            buffer = new StringBuilder(checked((int)capacity));
            length = SandboxNative.GetFinalPathNameByHandle(handle, buffer, capacity, 0);
            if (length == 0) throw SecurityError(Marshal.GetLastWin32Error());
            if (length >= capacity) throw new IOException("Final path exceeds buffer capacity.");
        }
        var resolved = Path.TrimEndingDirectorySeparator(NormalizeFinalPath(buffer.ToString(0, checked((int)length))));
        PluginPaths.CheckLinks(resolved);
        return resolved;
    }

    internal static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path[4..];
        return path;
    }

    // Strip inherited package-cache ACEs (such as S-1-15-3-<host package>) from host settings
    // and diagnostics without touching per-plugin ACL trees under Plugins/.
    public static void ProtectHostDataRoot(string root)
    {
        var fullRoot = NormalizePath(root);
        ProtectHostDataRootCore(fullRoot);
        lock (HostDataRootGate)
        {
            if (_requiredHostDataRoot is not null
                && string.Equals(_requiredHostDataRoot, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                _hostDataRootProtected = true;
                _hostDataRootError = null;
            }
        }
    }

    private static void ProtectHostDataRootCore(string root)
    {
        PluginPaths.CheckLinks(root);
        if (File.Exists(root)) throw new IOException("Host data root is not a directory.");
        // Validate direct files, Diagnostics, and host-owned plugin records before modifying any ACL
        // so hard-linked targets cannot be altered by inheritance or partial root hardening.
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root))
            {
                PluginPaths.CheckLinks(file);
                PluginFileSafety.CheckSingleLink(file);
            }
        }
        var diagnostics = Path.Combine(root, "Diagnostics");
        ValidateHostSubtree(diagnostics);
        var plugins = Path.Combine(root, "Plugins");
        if (File.Exists(plugins)) throw new IOException("Host plugins path is not a directory.");
        if (Directory.Exists(plugins))
        {
            PluginPaths.CheckLinks(plugins);
            foreach (var file in Directory.EnumerateFiles(plugins))
            {
                PluginPaths.CheckLinks(file);
                PluginFileSafety.CheckSingleLink(file);
            }
            foreach (var area in HostPluginAreas)
                ValidateHostSubtree(Path.Combine(plugins, area));
        }

        var owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
        ProtectHostDirectory(root);
        foreach (var file in Directory.EnumerateFiles(root))
        {
            PluginPaths.CheckLinks(file);
            PluginFileSafety.CheckSingleLink(file);
            ApplyFile(file, owner, null, false);
        }
        if (File.Exists(diagnostics)) throw new IOException("Host diagnostics path is not a directory.");
        if (Directory.Exists(diagnostics)) ProtectTree(diagnostics);
        else ProtectHostDirectory(diagnostics);
        if (File.Exists(plugins)) throw new IOException("Host plugins path is not a directory.");
        if (Directory.Exists(plugins))
        {
            ProtectHostDirectory(plugins);
            foreach (var file in Directory.EnumerateFiles(plugins))
            {
                PluginPaths.CheckLinks(file);
                PluginFileSafety.CheckSingleLink(file);
                ApplyFile(file, owner, null, false);
            }
            foreach (var area in HostPluginAreas)
            {
                var areaPath = Path.Combine(plugins, area);
                if (File.Exists(areaPath)) throw new IOException("Host plugin record path is not a directory.");
                if (Directory.Exists(areaPath)) ProtectTree(areaPath);
            }
        }
    }

    private static void ValidateHostSubtree(string directory)
    {
        if (File.Exists(directory)) throw new IOException("Expected a host directory.");
        if (Directory.Exists(directory))
        {
            PluginPaths.CheckTree(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                PluginFileSafety.CheckSingleLink(file);
        }
        else PluginPaths.CheckLinks(directory);
    }
    // Replace broad/inherited grants on private directories. Never add ALL APPLICATION PACKAGES.
    public static void ProtectTree(string directory, SecurityIdentifier? sandbox = null, bool writable = false)
    {
        PluginPaths.CheckTree(directory);
        // Check the complete tree before modifying any ACL: a hard link changes the target ACL too.
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) PluginFileSafety.CheckSingleLink(file);
        var owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
        ApplyDirectory(directory, owner, sandbox, writable);
        foreach (var item in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            PluginPaths.CheckLinks(item);
            if (Directory.Exists(item)) ApplyDirectory(item, owner, sandbox, writable);
            else ApplyFile(item, owner, sandbox, writable);
        }
        if (writable) LowLabel(directory);
    }

    private static void ApplyFile(string path, SecurityIdentifier owner, SecurityIdentifier? sandbox, bool writable)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var identity in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        if (sandbox is not null) acl.AddAccessRule(new(sandbox, writable ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
        if (writable) LowLabel(path);
    }

    private static void ApplyDirectory(string path, SecurityIdentifier owner, SecurityIdentifier? sandbox, bool writable, bool propagate = true)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var identity in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new(identity, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (sandbox is not null) acl.AddAccessRule(new(sandbox, writable ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (propagate) new DirectoryInfo(path).SetAccessControl(acl);
        else ApplyDirectorySelf(path, acl);
        if (writable) LowLabel(path);
    }

    private static void ApplyDirectorySelf(string path, DirectorySecurity acl)
    {
        // SetSecurityInfo on a MAXIMUM_ALLOWED directory handle preserves canonical filesystem
        // DACL protection (SE_DACL_PROTECTED | SE_DACL_AUTO_INHERITED) without propagating
        // inheritable ACEs to unchecked existing children (such as hard-linked files or Plugins/).
        SandboxNative.Check(SandboxNative.ConvertStringSecurityDescriptorToSecurityDescriptor(
            acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access), 1, out var descriptor, out _));
        try
        {
            SandboxNative.Check(SandboxNative.GetSecurityDescriptorDacl(descriptor, out var present, out var dacl, out _));
            if (!present) throw new InvalidOperationException();
            using var handle = SandboxNative.CreateFile(path, 0x02000000u, 7u, 0, 3u, 0x02200000u, 0);
            if (handle.IsInvalid) throw SecurityError(Marshal.GetLastWin32Error());
            var process = SandboxNative.GetCurrentProcess();
            if (!SandboxNative.DuplicateHandle(process, handle, process, out var accessCheck, 0x00040080u, false, 0))
                throw SecurityError(Marshal.GetLastWin32Error());
            accessCheck.Dispose();
            if (!SandboxNative.GetFileInformationByHandle(handle, out var info)) throw SecurityError(Marshal.GetLastWin32Error());
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new PluginException(PluginErrorCode.Path, "插件目录不能包含符号链接或重解析点。");
            if ((info.Attributes & (uint)FileAttributes.Directory) == 0)
                throw new IOException("Expected a directory.");
            var error = SandboxNative.SetSecurityInfo(handle, 1, 0x80000004u, 0, 0, dacl, 0);
            if (error != 0) throw SecurityError((int)error);
        }
        finally { SandboxNative.LocalFree(descriptor); }
    }

    private static Exception SecurityError(int error) => error switch
    {
        5 or 1307 or 1314 => new UnauthorizedAccessException(new Win32Exception(error).Message),
        2 or 3 => new DirectoryNotFoundException(new Win32Exception(error).Message),
        _ => new IOException(new Win32Exception(error).Message, unchecked((int)(0x80070000u | (uint)error)))
    };

    private static void LowLabel(string path)
    {
        SandboxNative.Check(SandboxNative.ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;OICI;NW;;;LW)", 1, out var descriptor, out _));
        try
        {
            SandboxNative.Check(SandboxNative.GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _));
            if (!present) throw new InvalidOperationException();
            var error = SandboxNative.SetNamedSecurityInfo(path, 1, 0x10, 0, 0, 0, sacl);
            if (error != 0) throw new Win32Exception((int)error);
        }
        finally { SandboxNative.LocalFree(descriptor); }
    }
}
