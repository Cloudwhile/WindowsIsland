using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace WindowsIsland.Plugins;

[SupportedOSPlatform("windows")]
internal static class SandboxAccess
{
    public static void ProtectHostDirectory(string directory)
    {
        PluginPaths.CheckLinks(directory);
        Directory.CreateDirectory(directory);
        ApplyDirectory(directory, WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException(), null, false);
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
            else
            {
                var acl = new FileSecurity();
                acl.SetAccessRuleProtection(true, false);
                foreach (var identity in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                    acl.AddAccessRule(new(identity, FileSystemRights.FullControl, AccessControlType.Allow));
                if (sandbox is not null) acl.AddAccessRule(new(sandbox, writable ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                new FileInfo(item).SetAccessControl(acl);
                if (writable) LowLabel(item);
            }
        }
        if (writable) LowLabel(directory);
    }

    private static void ApplyDirectory(string path, SecurityIdentifier owner, SecurityIdentifier? sandbox, bool writable)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var identity in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new(identity, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (sandbox is not null) acl.AddAccessRule(new(sandbox, writable ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
        if (writable) LowLabel(path);
    }

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
