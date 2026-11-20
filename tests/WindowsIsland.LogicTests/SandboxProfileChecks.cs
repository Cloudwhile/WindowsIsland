using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using WindowsIsland.Plugins;

namespace WindowsIsland.LogicTests;

internal static class SandboxProfileChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "sandbox-profile-" + Guid.NewGuid().ToString("N"));
        var longId = "wini_plugin_" + new string('a', 68);
        var profile = SandboxProcess.ProfileName(root, "wini_plugin_wechat");
        var longProfile = SandboxProcess.ProfileName(Path.Combine(root, new string('x', 80)), longId);
        check(profile.Length <= SandboxProcess.MaxProfileNameLength && longProfile.Length <= SandboxProcess.MaxProfileNameLength,
            "AppContainer profile names stay within the 50-character packaged child moniker limit");
        check(profile.StartsWith(SandboxProcess.ProfilePrefix, StringComparison.Ordinal)
            && profile[SandboxProcess.ProfilePrefix.Length..].All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')),
            "AppContainer profile names keep the expected prefix and valid lowercase hex suffix");
        check(profile == SandboxProcess.ProfileName(root.ToUpperInvariant(), "wini_plugin_wechat")
            && profile != SandboxProcess.ProfileName(root, "wini_plugin_telegram")
            && profile != SandboxProcess.ProfileName(root + "-other", "wini_plugin_wechat"),
            "AppContainer profile names normalize host root casing and isolate distinct roots and plugins");

        const string sampleFamily = "Microsoft.PowerShell_8wekyb3d8bbwe";
        const string expectedRids = "3474344596-1361774275-169937050-1715369765-655753896-4292765343-1302149271";
        check(SandboxProcess.PackageFamilyRids(sampleFamily) == expectedRids
            && SandboxProcess.PackageFamilyRids(sampleFamily.ToUpperInvariant()) == expectedRids,
            "Package family SID derivation matches Windows case-insensitive SHA-256 RID layout");

        if (!OperatingSystem.IsWindows()) return;
        RunWindowsChecks(check, root, profile);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RunWindowsChecks(Action<bool, string> check, string root, string profile)
    {
        const string sampleFamily = "Microsoft.PowerShell_8wekyb3d8bbwe";
        const string expectedRids = "3474344596-1361774275-169937050-1715369765-655753896-4292765343-1302149271";
        var parentSid = new SecurityIdentifier("S-1-15-2-" + expectedRids);
        var childSid = new SecurityIdentifier("S-1-15-2-" + expectedRids + "-11-22-33-44");
        var otherSid = new SecurityIdentifier("S-1-15-2-1-2-3-4-5-6-7");
        var parentCap = new SecurityIdentifier("S-1-15-3-" + expectedRids);
        var internetCap = new SecurityIdentifier("S-1-15-3-1");
        var otherCap = new SecurityIdentifier("S-1-15-3-1-2-3-4-5-6-7");

        check(SandboxProcess.ImplicitParentPackageCapability(otherSid, null) is null,
            "Unpackaged hosts allow no implicit sandbox capabilities");
        check(SandboxProcess.ImplicitParentPackageCapability(childSid, sampleFamily)?.Equals(parentCap) == true,
            "Packaged hosts derive only the host package's implicit capability SID for child AppContainers");
        check(ThrowsInvalidOperation(() => SandboxProcess.ImplicitParentPackageCapability(parentSid, sampleFamily)),
            "Packaged hosts reject a non-child parent package SID");
        check(ThrowsInvalidOperation(() => SandboxProcess.ImplicitParentPackageCapability(otherSid, sampleFamily)),
            "Packaged hosts reject an AppContainer SID outside the host package");

        VerifyCapabilityBuffer(check, null, [], expectValid: true, "Unpackaged host accepts zero capabilities");
        VerifyCapabilityBuffer(check, null, [parentCap], expectValid: false, "Unpackaged host rejects any capability");
        VerifyCapabilityBuffer(check, parentCap, [], expectValid: true, "Packaged host accepts zero capabilities");
        VerifyCapabilityBuffer(check, parentCap, [parentCap], expectValid: true,
            "Packaged host accepts only the kernel-attached host package capability");
        VerifyCapabilityBuffer(check, parentCap, [internetCap], expectValid: false,
            "Packaged host rejects internetClient capability");
        VerifyCapabilityBuffer(check, parentCap, [otherCap], expectValid: false,
            "Packaged host rejects foreign package capability");
        VerifyCapabilityBuffer(check, parentCap, [parentCap, internetCap], expectValid: false,
            "Packaged host rejects additional capabilities alongside the host package capability");
        VerifyTruncatedCapabilityBuffer(check, parentCap);

        // Verify ProtectHostDataRoot strips inherited packaged-cache capability ACEs without clobbering plugin ACLs.
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var diagnosticsDir = Path.Combine(root, "Diagnostics");
            var diagnosticsFile = Path.Combine(diagnosticsDir, "diag.json");
            var pluginDataDir = Path.Combine(root, "Plugins", "data", "sample");
            var pluginDataFile = Path.Combine(pluginDataDir, "keep.txt");
            Directory.CreateDirectory(diagnosticsDir);
            Directory.CreateDirectory(pluginDataDir);
            File.WriteAllText(settingsPath, "{}");
            File.WriteAllText(diagnosticsFile, "{}");
            File.WriteAllText(pluginDataFile, "keep");

            AddAllowAce(root, parentCap);
            AddAllowAce(diagnosticsDir, parentCap);
            AddFileAllowAce(settingsPath, parentCap);
            AddFileAllowAce(diagnosticsFile, parentCap);
            AddFileAllowAce(pluginDataFile, childSid);

            SandboxAccess.ProtectHostDataRoot(root);
            check(!HasDirectorySid(root, parentCap) && !HasDirectorySid(diagnosticsDir, parentCap)
                && !HasFileSid(settingsPath, parentCap) && !HasFileSid(diagnosticsFile, parentCap),
                "ProtectHostDataRoot strips inherited host package capability ACEs from host data and diagnostics");
            check(HasFileSid(pluginDataFile, childSid),
                "ProtectHostDataRoot preserves per-plugin ACLs under Plugins");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        // Verify live Win32 profile create / existing-profile derive / delete cycle with the new <=50-char name.
        nint createdSid = 0, derivedSid = 0;
        try
        {
            var createResult = SandboxNative.CreateAppContainerProfile(profile, profile, "Windows Island isolated plugin", 0, 0, out createdSid);
            Marshal.ThrowExceptionForHR(createResult);
            var duplicateResult = SandboxNative.CreateAppContainerProfile(profile, profile, "Windows Island isolated plugin", 0, 0, out _);
            check(duplicateResult == unchecked((int)0x800700B7), "Recreating an existing AppContainer profile reports ERROR_ALREADY_EXISTS");
            Marshal.ThrowExceptionForHR(SandboxNative.DeriveAppContainerSidFromAppContainerName(profile, out derivedSid));
            check(new SecurityIdentifier(createdSid).Equals(new SecurityIdentifier(derivedSid)),
                "DeriveAppContainerSidFromAppContainerName resolves the exact SID created for the profile");
        }
        finally
        {
            if (createdSid != 0) SandboxNative.FreeSid(createdSid);
            if (derivedSid != 0) SandboxNative.FreeSid(derivedSid);
            DeleteAppContainerProfile(profile);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void VerifyCapabilityBuffer(Action<bool, string> check, SecurityIdentifier? allowed, SecurityIdentifier[] sids, bool expectValid, string label)
    {
        var header = nint.Size;
        var entrySize = Marshal.SizeOf<SandboxNative.SidAndAttributes>();
        var totalSize = header + sids.Length * entrySize;
        var memory = Marshal.AllocHGlobal(totalSize);
        var sidPointers = new List<nint>();
        try
        {
            Marshal.WriteInt32(memory, sids.Length);
            for (var index = 0; index < sids.Length; index++)
            {
                var bytes = new byte[sids[index].BinaryLength];
                sids[index].GetBinaryForm(bytes, 0);
                var sidMemory = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, sidMemory, bytes.Length);
                sidPointers.Add(sidMemory);
                Marshal.StructureToPtr(new SandboxNative.SidAndAttributes { Sid = sidMemory, Attributes = 0x4 },
                    memory + header + index * entrySize, false);
            }
            var threw = ThrowsInvalidOperation(() => SandboxProcess.VerifyCapabilities(memory, (uint)totalSize, allowed));
            check(expectValid ? !threw : threw, label);
        }
        finally
        {
            foreach (var pointer in sidPointers) Marshal.FreeHGlobal(pointer);
            Marshal.FreeHGlobal(memory);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void VerifyTruncatedCapabilityBuffer(Action<bool, string> check, SecurityIdentifier allowed)
    {
        var memory = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(memory, 1);
            check(ThrowsInvalidOperation(() => SandboxProcess.VerifyCapabilities(memory, sizeof(int), allowed)),
                "Truncated capability buffers are rejected");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static bool ThrowsInvalidOperation(Action action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddAllowAce(string directory, SecurityIdentifier sid)
    {
        var info = new DirectoryInfo(directory);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddFileAllowAce(string file, SecurityIdentifier sid)
    {
        var info = new FileInfo(file);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool HasDirectorySid(string directory, SecurityIdentifier sid) =>
        new DirectoryInfo(directory).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().Any(rule => rule.IdentityReference.Equals(sid));

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool HasFileSid(string file, SecurityIdentifier sid) =>
        new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().Any(rule => rule.IdentityReference.Equals(sid));

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeleteAppContainerProfile(string name);
}
