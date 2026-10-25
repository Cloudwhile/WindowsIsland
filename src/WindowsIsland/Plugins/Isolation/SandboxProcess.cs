using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Runtime.Versioning;
using WindowsIsland.PluginIsolation;

namespace WindowsIsland.Plugins;

// Fail closed: the worker is resumed only after its OS token and enclosing job have been verified.
internal sealed class SandboxProcess : IDisposable
{
    private nint _process, _job;
    private readonly object _processGate = new();
    private int _disposed;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task ExitTask => _exited.Task;
    public AnonymousPipeServerStream Input { get; } = new(PipeDirection.In, HandleInheritability.Inheritable);
    public AnonymousPipeServerStream Output { get; } = new(PipeDirection.Out, HandleInheritability.Inheritable);
    public int ProcessId { get; private set; }
    public string Sid { get; private set; } = "";
    public uint? ExitCode { get { lock (_processGate) return _process != 0 && SandboxNative.GetExitCodeProcess(_process, out var code) && code != 259 ? code : null; } }

    public SandboxProcess(ExternalPlugin plugin)
    {
        try { Launch(plugin); }
        catch
        {
            Dispose();
            // 创建由后台线程执行；失败也必须确认已回收，不能让调用方提前换包。
            try { ExitTask.GetAwaiter().GetResult(); }
            catch (Exception error) { throw new TimeoutException("The failed sandbox could not be reclaimed.", error); }
            throw;
        }
    }

    private void Launch(ExternalPlugin plugin)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        PluginPaths.CheckTree(plugin.Directory);
        Directory.CreateDirectory(plugin.DataDirectory);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(plugin.HostRoot).ToUpperInvariant() + "\n" + plugin.Manifest.Id)))[..40].ToLowerInvariant();
        var profile = "WindowsIsland.Plugin." + identity;
        var result = SandboxNative.CreateAppContainerProfile(profile, profile, "Windows Island isolated plugin", 0, 0, out var sid);
        if (result == unchecked((int)0x800700B7)) result = SandboxNative.DeriveAppContainerSidFromAppContainerName(profile, out sid);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            var principal = new SecurityIdentifier(sid);
            Sid = principal.Value;
            // Give this SID no access to siblings or host state. Own code stays read only.
            SandboxAccess.ProtectTree(plugin.Directory, principal);
            SandboxAccess.ProtectTree(plugin.DataDirectory, principal, writable: true);
            var runtime = PrepareRuntime(plugin, identity, principal);
            var executable = Path.Combine(runtime, "WindowsIsland.PluginWorker.exe");
            var temporary = Path.Combine(plugin.DataDirectory, "temp");
            Directory.CreateDirectory(temporary);
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["windir"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["SystemDrive"] = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\'),
                ["TEMP"] = temporary, ["TMP"] = temporary,
                ["USERPROFILE"] = plugin.DataDirectory, ["LOCALAPPDATA"] = plugin.DataDirectory, ["APPDATA"] = plugin.DataDirectory,
                ["DOTNET_EnableDiagnostics"] = "0", ["DOTNET_EnableDiagnostics_IPC"] = "0",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["COMPlus_EnableDiagnostics"] = "0",
                ["DOTNET_DISABLE_GUI_ERRORS"] = "1"
            };
            var environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
            nint attributes = 0;
            var allocations = new List<nint>();
            nint thread = 0;
            try
            {
                nuint bytes = 0;
                SandboxNative.InitializeProcThreadAttributeList(0, 4, 0, ref bytes);
                attributes = Marshal.AllocHGlobal(checked((int)bytes));
                SandboxNative.Check(SandboxNative.InitializeProcThreadAttributeList(attributes, 4, 0, ref bytes));
                void Attribute<T>(nuint name, T value) where T : struct
                {
                    var memory = Marshal.AllocHGlobal(Marshal.SizeOf<T>()); allocations.Add(memory);
                    Marshal.StructureToPtr(value, memory, false);
                    SandboxNative.Check(SandboxNative.UpdateProcThreadAttribute(attributes, 0, name, memory, (nuint)Marshal.SizeOf<T>(), 0, 0));
                }
                Attribute(0x20009, new SandboxNative.SecurityCapabilities { Sid = sid });
                Attribute(0x2000F, 1u); // ALL_APPLICATION_PACKAGES_OPT_OUT: LPAC.
                Attribute(0x2000E, 1u); // PROCESS_CREATION_CHILD_PROCESS_RESTRICTED.
                // The CLR bootstrap stays in the trusted runtime directory. LPAC and all Job
                // restrictions are effective from creation; no plugin code path is passed yet.
                var handles = Marshal.AllocHGlobal(2 * nint.Size); allocations.Add(handles);
                Marshal.WriteIntPtr(handles, 0, Input.ClientSafePipeHandle.DangerousGetHandle());
                Marshal.WriteIntPtr(handles, nint.Size, Output.ClientSafePipeHandle.DangerousGetHandle());
                SandboxNative.Check(SandboxNative.UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(2 * nint.Size), 0, 0));
                var startup = new SandboxNative.StartupInfoEx { Info = new() { Size = (uint)Marshal.SizeOf<SandboxNative.StartupInfoEx>() }, Attributes = attributes };
                var command = new StringBuilder('"' + executable + "\" --read-handle " + Output.GetClientHandleAsString() + " --write-handle " + Input.GetClientHandleAsString());
                SandboxNative.Check(SandboxNative.CreateProcess(executable, command, 0, 0, true, 0x08080404, environment, runtime, ref startup, out var process));
                _process = process.Process; thread = process.Thread; ProcessId = checked((int)process.ProcessId);
                VerifyToken(principal);
                _job = SandboxNative.CreateJobObject(0, null);
                if (_job == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                var limits = new SandboxNative.ExtendedLimits { Basic = new() { Flags = 0x8 | 0x100 | 0x2000 | 0x400, ActiveProcesses = 1 }, ProcessMemory = 768 * 1024 * 1024 };
                SetJob(9, limits);
                SetJob(4, 0xffu);
                SetJob(15, new SandboxNative.CpuLimits { Flags = 5, Rate = 2000 });
                SandboxNative.Check(SandboxNative.AssignProcessToJobObject(_job, _process));
                Input.DisposeLocalCopyOfClientHandle(); Output.DisposeLocalCopyOfClientHandle();
                if (SandboxNative.ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (thread != 0) SandboxNative.CloseHandle(thread);
                if (attributes != 0) { SandboxNative.DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
                foreach (var memory in allocations) Marshal.FreeHGlobal(memory);
                Marshal.FreeHGlobal(environment);
            }
        }
        finally { SandboxNative.FreeSid(sid); }
    }

    private void SetJob<T>(int type, T value) where T : struct
    {
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try { Marshal.StructureToPtr(value, memory, false); SandboxNative.Check(SandboxNative.SetInformationJobObject(_job, type, memory, (uint)Marshal.SizeOf<T>())); }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public async Task VerifyReadyAsync(IsolationRpc rpc, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (!await rpc.CallAsync<bool>("ready", token: timeout.Token)) throw new InvalidOperationException("Sandbox bootstrap failed.");
        }
        catch (Exception error)
        {
            throw new IOException("Sandbox bootstrap failed with native status " + (ExitCode?.ToString("X8") ?? "unavailable"), error);
        }
        lock (_processGate)
        {
            if (_process == 0 || Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(SandboxProcess));
            VerifyToken(new SecurityIdentifier(Sid));
        }
    }

    [SupportedOSPlatform("windows")]
    private void VerifyToken(SecurityIdentifier expected)
    {
        SandboxNative.Check(SandboxNative.OpenProcessToken(_process, 10, out var token));
        try
        {
            void Read(int kind, Action<nint> verify)
            {
                SandboxNative.GetTokenInformation(token, kind, 0, 0, out var size);
                if (size is 0 or > 65536) throw new InvalidOperationException($"Invalid sandbox token class {kind}, error {Marshal.GetLastWin32Error()}.");
                var memory = Marshal.AllocHGlobal((int)size);
                try { SandboxNative.Check(SandboxNative.GetTokenInformation(token, kind, memory, size, out _)); verify(memory); }
                finally { Marshal.FreeHGlobal(memory); }
            }
            Read(29, memory => { if (Marshal.ReadInt32(memory) != 1) throw new InvalidOperationException("AppContainer required."); });
            VerifyPackageIsolation(token, expected);
            Read(31, memory => { if (!new SecurityIdentifier(Marshal.ReadIntPtr(memory)).Equals(expected)) throw new InvalidOperationException("Sandbox identity mismatch."); });
            Read(30, memory => { if (Marshal.ReadInt32(memory) != 0) throw new InvalidOperationException("Sandbox capabilities must be empty."); });
        }
        finally { SandboxNative.CloseHandle(token); }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyPackageIsolation(nint token, SecurityIdentifier expected)
    {
        // Class 46 is rejected by GetTokenInformation on some supported Windows builds.
        // Verify the actual access-check boundary instead: its own SID must work while the
        // broad ALL APPLICATION PACKAGES grant must not match this token.
        SandboxNative.Check(SandboxNative.DuplicateToken(token, 2, out var impersonation));
        try
        {
            bool CanAccess(string package)
            {
                SandboxNative.Check(SandboxNative.ConvertStringSecurityDescriptorToSecurityDescriptor(
                    "O:SYG:SYD:(A;;0x1;;;AU)(A;;0x1;;;" + package + ")", 1, out var descriptor, out _));
                var privileges = Marshal.AllocHGlobal(4096);
                try
                {
                    uint size = 4096;
                    var mapping = new SandboxNative.GenericMapping { Read = 1, Write = 2, Execute = 4, All = 7 };
                    SandboxNative.Check(SandboxNative.AccessCheck(descriptor, impersonation, 1, ref mapping, privileges, ref size, out _, out var allowed));
                    return allowed;
                }
                finally { Marshal.FreeHGlobal(privileges); SandboxNative.LocalFree(descriptor); }
            }
            if (CanAccess("AC") || !CanAccess(expected.Value)) throw new InvalidOperationException("LPAC access isolation is required.");
        }
        finally { SandboxNative.CloseHandle(impersonation); }
    }

    [SupportedOSPlatform("windows")]
    private static string PrepareRuntime(ExternalPlugin plugin, string identity, SecurityIdentifier sid)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "PluginWorker");
        PluginPaths.CheckTree(source);
        if (!File.Exists(Path.Combine(source, "WindowsIsland.PluginWorker.exe"))) throw new FileNotFoundException("The isolated plugin runtime is unavailable.");
        // Private copies avoid changing ACLs under Program Files or granting access to the host binary.
        var runtime = Path.Combine(plugin.HostRoot, "sandbox-runtime", identity);
        PluginPaths.CheckLinks(runtime);
        Directory.CreateDirectory(runtime);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            PluginFileSafety.CheckSingleLink(file);
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(runtime, relative);
            PluginPaths.CheckLinks(target);
            if (File.Exists(target)) PluginFileSafety.CheckSingleLink(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(file).Length
                || !SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(file)))) File.Copy(file, target, true);
        }
        SandboxAccess.ProtectTree(runtime, sid);
        return runtime;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        nint process;
        lock (_processGate) process = Interlocked.Exchange(ref _process, 0);
        if (process != 0) SandboxNative.TerminateProcess(process, 0);
        if (_job != 0) { SandboxNative.CloseHandle(_job); _job = 0; }
        Input.Dispose(); Output.Dispose();
        if (process == 0) { _exited.TrySetResult(); return; }
        _ = Task.Run(() =>
        {
            var confirmed = false;
            try
            {
                var result = SandboxNative.WaitForSingleObject(process, 5000);
                if (result == 258) throw new TimeoutException("The isolated plugin did not exit within the limit.");
                if (result != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                confirmed = true;
                _exited.TrySetResult();
            }
            catch (Exception error)
            {
                _exited.TrySetException(error);
                // 换包立即收到失败，原生句柄仍保留到 OS 确认真实退出。
                if (!confirmed) SandboxNative.WaitForSingleObject(process, uint.MaxValue);
            }
            finally { SandboxNative.CloseHandle(process); }
        });
    }
}
