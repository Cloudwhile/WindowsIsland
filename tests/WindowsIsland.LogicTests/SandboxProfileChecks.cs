using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class SandboxProfileChecks
{
    public static async Task RunAsync(Action<bool, string> check)
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
        await RunWindowsChecksAsync(check, root, profile);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task RunWindowsChecksAsync(Action<bool, string> check, string root, string profile)
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
            var hostConsentDir = Path.Combine(root, "Plugins", "host-consents");
            var hostConsentFile = Path.Combine(hostConsentDir, "wini_plugin_peer.json");
            var hostSettingsDir = Path.Combine(root, "Plugins", "host-settings");
            var hostSettingsFile = Path.Combine(hostSettingsDir, "wini_plugin_peer.json");
            var pluginDataDir = Path.Combine(root, "Plugins", "data", "sample");
            var pluginDataFile = Path.Combine(pluginDataDir, "keep.txt");
            Directory.CreateDirectory(diagnosticsDir);
            Directory.CreateDirectory(hostConsentDir);
            Directory.CreateDirectory(hostSettingsDir);
            Directory.CreateDirectory(pluginDataDir);
            File.WriteAllText(settingsPath, "{}");
            File.WriteAllText(diagnosticsFile, "{}");
            File.WriteAllText(hostConsentFile, "{}");
            File.WriteAllText(hostSettingsFile, "{}");
            File.WriteAllText(pluginDataFile, "keep");

            AddAllowAce(root, parentCap);
            AddAllowAce(diagnosticsDir, parentCap);
            AddAllowAce(hostConsentDir, parentCap);
            AddAllowAce(hostSettingsDir, parentCap);
            AddFileAllowAce(settingsPath, parentCap);
            AddFileAllowAce(diagnosticsFile, parentCap);
            AddFileAllowAce(hostConsentFile, parentCap);
            AddFileAllowAce(hostSettingsFile, parentCap);
            AddFileAllowAce(pluginDataFile, childSid);

            SandboxAccess.ProtectHostDataRoot(root);
            check(!HasDirectorySid(root, parentCap) && !HasDirectorySid(diagnosticsDir, parentCap)
                && !HasFileSid(settingsPath, parentCap) && !HasFileSid(diagnosticsFile, parentCap)
                && !HasDirectorySid(hostConsentDir, parentCap) && !HasFileSid(hostConsentFile, parentCap)
                && !HasDirectorySid(hostSettingsDir, parentCap) && !HasFileSid(hostSettingsFile, parentCap),
                "ProtectHostDataRoot strips inherited host package capability ACEs from host data, diagnostics, and host-owned plugin records");
            check(HasFileSid(pluginDataFile, childSid),
                "ProtectHostDataRoot preserves per-plugin ACLs under Plugins");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        VerifyHardLinkProtection(check, parentCap, otherCap);
        await VerifyHostDataRootLaunchGateAsync(check, parentCap);

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

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void VerifyHardLinkProtection(Action<bool, string> check, SecurityIdentifier parentCap, SecurityIdentifier otherCap)
    {
        var baseDir = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "sandbox-hardlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var outsideDir = Path.Combine(baseDir, "outside");
            Directory.CreateDirectory(outsideDir);
            var targetFile = Path.Combine(outsideDir, "victim.txt");
            File.WriteAllText(targetFile, "outside");
            AddAllowAce(outsideDir, parentCap);
            var initialTargetSddl = GetFileSddl(targetFile);
            check(HasFileSid(targetFile, parentCap), "External hard-link target starts with its inherited test ACE");

            // 1) Hard link directly inside the host data root must be rejected before any ACL change.
            var rootA = Path.Combine(baseDir, "root-a");
            var diagA = Path.Combine(rootA, "Diagnostics");
            Directory.CreateDirectory(diagA);
            var settingsA = Path.Combine(rootA, "settings.json");
            File.WriteAllText(settingsA, "{}");
            AddAllowAce(rootA, parentCap);
            var linkA = Path.Combine(rootA, "linked.txt");
            if (!CreateHardLink(linkA, targetFile, 0)) throw new IOException("CreateHardLink failed: " + Marshal.GetLastWin32Error());

            check(ThrowsPluginPathError(() => SandboxAccess.ProtectHostDataRoot(rootA)),
                "ProtectHostDataRoot rejects hard links in the host data root");
            check(GetFileSddl(targetFile) == initialTargetSddl && HasDirectorySid(rootA, parentCap),
                "Rejecting a root hard link leaves both the external target ACL and root ACL untouched");

            // 2) Hard link inside Diagnostics must also be rejected before any ACL change.
            var rootB = Path.Combine(baseDir, "root-b");
            var diagB = Path.Combine(rootB, "Diagnostics");
            Directory.CreateDirectory(diagB);
            var settingsB = Path.Combine(rootB, "settings.json");
            File.WriteAllText(settingsB, "{}");
            AddAllowAce(rootB, parentCap);
            var linkB = Path.Combine(diagB, "linked.json");
            if (!CreateHardLink(linkB, targetFile, 0)) throw new IOException("CreateHardLink failed: " + Marshal.GetLastWin32Error());

            check(ThrowsPluginPathError(() => SandboxAccess.ProtectHostDataRoot(rootB)),
                "ProtectHostDataRoot rejects hard links inside Diagnostics");
            check(GetFileSddl(targetFile) == initialTargetSddl && HasFileSid(settingsB, parentCap),
                "Rejecting a Diagnostics hard link leaves external targets and root files untouched");

            // 3) ProtectHostDirectory writes only the directory DACL via SetSecurityInfo and keeps DACL protection across parent updates.
            var parentC = Path.Combine(baseDir, "parent-c");
            var rootC = Path.Combine(parentC, "root-c");
            var diagC = Path.Combine(rootC, "Diagnostics");
            var siblingC = Path.Combine(rootC, "Other");
            Directory.CreateDirectory(diagC);
            Directory.CreateDirectory(siblingC);
            var settingsC = Path.Combine(rootC, "settings.json");
            var diagFileC = Path.Combine(diagC, "diag.json");
            File.WriteAllText(settingsC, "{}");
            File.WriteAllText(diagFileC, "{}");
            AddAllowAce(rootC, parentCap);
            var siblingSddlBefore = GetDirectorySddl(siblingC);
            var linkC = Path.Combine(siblingC, "linked.txt");
            if (!CreateHardLink(linkC, targetFile, 0)) throw new IOException("CreateHardLink failed: " + Marshal.GetLastWin32Error());

            SandboxAccess.ProtectHostDataRoot(rootC);
            AddAllowAce(parentC, otherCap);
            var rootCAcl = new DirectoryInfo(rootC).GetAccessControl();
            var createdAfter = Path.Combine(rootC, "after.txt");
            File.WriteAllText(createdAfter, "new");
            check(rootCAcl.AreAccessRulesProtected && !HasDirectorySid(rootC, parentCap) && !HasDirectorySid(rootC, otherCap)
                && !HasFileSid(settingsC, parentCap) && !HasDirectorySid(diagC, parentCap)
                && !HasFileSid(diagFileC, parentCap) && !HasFileSid(createdAfter, parentCap) && !HasFileSid(createdAfter, otherCap),
                "SetSecurityInfo keeps the host root DACL protected after parent ACE updates and hardens host files");
            check(GetDirectorySddl(siblingC) == siblingSddlBefore && GetFileSddl(targetFile) == initialTargetSddl,
                "ProtectHostDataRoot leaves existing unchecked subdirectory DACLs and hard-linked external files untouched");
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task VerifyHostDataRootLaunchGateAsync(Action<bool, string> check, SecurityIdentifier parentCap)
    {
        var baseDir = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "sandbox-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var outsideDir = Path.Combine(baseDir, "outside");
            var root = Path.Combine(baseDir, "host");
            var diagDir = Path.Combine(root, "Diagnostics");
            Directory.CreateDirectory(outsideDir);
            Directory.CreateDirectory(diagDir);
            var targetFile = Path.Combine(outsideDir, "victim.txt");
            var settingsFile = Path.Combine(root, "settings.json");
            var diagFile = Path.Combine(diagDir, "diagnostics.json");
            File.WriteAllText(targetFile, "outside");
            File.WriteAllText(settingsFile, """{"SystemNotifications":false,"Power":true,"WeChat":false,"Telegram":false}""");
            File.WriteAllText(diagFile, "[]");
            AddAllowAce(root, parentCap);
            var linkPath = Path.Combine(root, "blocked-link.txt");
            if (!CreateHardLink(linkPath, targetFile, 0)) throw new IOException("CreateHardLink failed: " + Marshal.GetLastWin32Error());

            // Prepare a consented external plugin alongside a built-in plugin.
            var paths = new PluginPaths(Path.Combine(root, "Plugins"));
            const string id = "wini_plugin_gate_test";
            var manifest = new PluginManifest(id, "Gate test plugin", "1.0.0", "WindowsIsland.PluginSecurityFixture.dll",
                "WindowsIsland.PluginSecurityFixture.LifecycleSource", 1, [PluginPermissions.Settings]);
            var code = paths.Code(PluginSourceKind.Manual, id);
            Directory.CreateDirectory(code);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixture", manifest.EntryAssembly), Path.Combine(code, manifest.EntryAssembly));
            File.WriteAllText(Path.Combine(code, "manifest.json"), JsonSerializer.Serialize(manifest, PluginManifestReader.Json));
            var external = new ExternalPlugin(paths, manifest, code);
            PluginConsentPolicy.Grant(external, PluginConsentPolicy.Describe(external));
            var consentFile = Path.Combine(paths.Root, "host-consents", id + ".json");
            AddFileAllowAce(consentFile, parentCap);
            check(HasFileSid(consentFile, parentCap) && PluginConsentPolicy.HasConsent(external) && !HasFileSid(consentFile, parentCap),
                "Reading an existing host-consents receipt strips inherited host package capability ACEs");

            // Simulate Program.Main startup protection failure.
            SandboxAccess.RequireHostDataRootProtection(root);
            Exception? startupError = null;
            try { SandboxAccess.ProtectHostDataRoot(root); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PluginException)
            {
                startupError = error;
                SandboxAccess.RecordHostDataRootFailure(error);
            }
            check(startupError is PluginException && SandboxAccess.IsHostDataRootBlocked
                && ReferenceEquals(SandboxAccess.HostDataRootError, startupError),
                "Host data root protection failure is recorded instead of ignored");
            check(ThrowsInvalidOperation(() => SandboxAccess.EnsureHostDataRootProtected()),
                "EnsureHostDataRootProtected blocks while host data root protection cannot complete");
            check(ThrowsInvalidOperation(() => external.Create()),
                "ExternalPlugin.Create refuses to create a plugin proxy while host data root protection is incomplete");
            check(ThrowsInvalidOperation(() => _ = new SandboxProcess(new(manifest, paths.Root, code, external.DataDirectory))),
                "SandboxProcess refuses to launch while host data root protection is incomplete");

            var store = new SettingsStore(settingsFile);
            store.SetPluginBoolean(id, "Host.Enabled", true);
            var builtInStarted = false;
            var externalCreated = false;
            var metadata = new PluginMetadata(false, manifest.Version, Path.Combine(code, manifest.EntryAssembly), manifest.Author, external);
            var externalDefinition = new NotificationPluginDefinition(id, manifest.Name, "PluginToggle." + id, 0xE8A5,
                DiagnosticArea.Application, _ => store.GetPluginBoolean(id, "Host.Enabled", false),
                (s, enabled) => { store.SetPluginBoolean(id, "Host.Enabled", enabled); return s; }, _ => false, metadata);
            using var manager = new PluginManager(
            [
                new(BuiltInPluginDefinitions.Power, () => new StubSource(BuiltInPluginIds.Power, () => builtInStarted = true)),
                new(externalDefinition, () => { externalCreated = true; return new StubSource(id, () => { }); })
            ], store);
            await manager.StartAsync();
            var externalStatus = manager.Snapshot().Single(item => item.Definition.Id == id);
            check(builtInStarted && !externalCreated && externalStatus.StatusKey == "Unavailable" && externalStatus.CanRetry,
                "PluginManager keeps built-in plugins running while blocking external plugins until root protection succeeds");

            // Once the offending hard link is removed, retrying protects the root + Diagnostics and unblocks launch.
            File.Delete(linkPath);
            await manager.RetryAsync(id);
            check(!SandboxAccess.IsHostDataRootBlocked && SandboxAccess.HostDataRootError is null && externalCreated,
                "Retrying after fixing the root completes protection and unblocks external plugin launch");
            check(!HasDirectorySid(root, parentCap) && !HasFileSid(settingsFile, parentCap)
                && !HasDirectorySid(diagDir, parentCap) && !HasFileSid(diagFile, parentCap),
                "Recovery protection strips host package capability ACEs from all host files and Diagnostics before launch");

            // Verification mode explicitly disables external plugin startup even without a host data root.
            await manager.SetEnabledAsync(id, false);
            externalCreated = false;
            SandboxAccess.DisableExternalPlugins();
            check(SandboxAccess.IsHostDataRootBlocked
                && ThrowsInvalidOperation(() => SandboxAccess.EnsureHostDataRootProtected())
                && ThrowsInvalidOperation(() => external.Create())
                && ThrowsInvalidOperation(() => _ = new SandboxProcess(new(manifest, paths.Root, code, external.DataDirectory))),
                "Verification mode disables external plugin creation and sandbox launch");
            await manager.SetEnabledAsync(id, true);
            var verifyBlockedStatus = manager.Snapshot().Single(item => item.Definition.Id == id);
            check(!externalCreated && verifyBlockedStatus.StatusKey == "Unavailable",
                "PluginManager blocks external plugins when verification mode disables external plugin execution");

            // Stale pre-create failure after EnableRevision changes must not poison entry.Failed.
            SandboxAccess.ResetHostDataRootProtection();
            var createEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStaleCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var createAttempts = 0;
            var freshStarted = false;
            const string staleId = "wini_plugin_stale_revision";
            store.SetPluginBoolean(staleId, "Host.Enabled", true);
            var staleDefinition = new NotificationPluginDefinition(staleId, "Stale revision test", "PluginToggle." + staleId, 0xE8A5,
                DiagnosticArea.Application, _ => store.GetPluginBoolean(staleId, "Host.Enabled", false),
                (s, enabled) => { store.SetPluginBoolean(staleId, "Host.Enabled", enabled); return s; }, _ => false, metadata);
            using var staleManager = new PluginManager(
            [
                new(staleDefinition, () =>
                {
                    if (Interlocked.Increment(ref createAttempts) == 1)
                    {
                        createEntered.TrySetResult();
                        releaseStaleCreate.Task.GetAwaiter().GetResult();
                        throw new InvalidOperationException("Stale pre-create failure.");
                    }
                    return new StubSource(staleId, () => freshStarted = true);
                })
            ], store);
            var firstStart = staleManager.StartAsync();
            await createEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var reenableTask = Task.Run(async () =>
            {
                await staleManager.SetEnabledAsync(staleId, false);
                await staleManager.SetEnabledAsync(staleId, true);
            });
            // Wait until SetEnabledAsync(false) increments EnableRevision, then let the stale attempt throw.
            var waitDeadline = DateTime.UtcNow.AddSeconds(5);
            while (store.GetPluginBoolean(staleId, "Host.Enabled", true) && DateTime.UtcNow < waitDeadline)
                await Task.Delay(10);
            releaseStaleCreate.TrySetResult();
            await Task.WhenAll(firstStart, reenableTask).WaitAsync(TimeSpan.FromSeconds(5));
            check(freshStarted && staleManager.Snapshot().Single().StatusKey != "Unavailable",
                "Stale pre-create exceptions after EnableRevision changes are discarded without marking the plugin failed");
        }
        finally
        {
            SandboxAccess.ResetHostDataRootProtection();
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
        }
    }

    private sealed class StubSource(string id, Action onStart) : IMessageSource
    {
        public string Id { get; } = id;
        public string DisplayName { get; } = id;
        public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
        public Task InitializeAsync(IPluginContext context) => Task.CompletedTask;
        public Task StartAsync() { onStart(); return Task.CompletedTask; }
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    private static bool ThrowsPluginPathError(Action action)
    {
        try { action(); return false; }
        catch (PluginException error) { return error.Code == PluginErrorCode.Path; }
    }

    private static bool ThrowsInvalidOperation(Action action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string GetDirectorySddl(string directory) =>
        new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string GetFileSddl(string file) =>
        new FileInfo(file).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

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
