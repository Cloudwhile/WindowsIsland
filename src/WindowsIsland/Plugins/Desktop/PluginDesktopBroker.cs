using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

// Application parsing stays in the worker. This broker only mediates declared OS resources.
internal sealed partial class PluginDesktopBroker : IPluginDesktop, IPluginDesktopProcessScope, IDisposable, IAsyncDisposable
{
    private sealed class Module(ProcessModule source, FileStream file)
    {
        public readonly string Name = source.ModuleName;
        public readonly nint Address = source.BaseAddress;
        public readonly int Size = source.ModuleMemorySize;
        public readonly FileStream File = file;
    }
    private sealed class Client(Process process, SafeProcessHandle handle, long startedAt)
    {
        public readonly Process Process = process;
        public readonly SafeProcessHandle Handle = handle;
        public readonly long StartedAt = startedAt;
        public readonly Dictionary<string, Module> Modules = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<(string, long), DesktopFlag> Changed = [];
        public byte[]? Icon;
    }
    private readonly string _hostRoot;
    private readonly PluginDesktopScope _scope;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<int, Client> _clients = [];
    private int[] _processIds = [];
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _revoked;

    public PluginDesktopBroker(PluginManifest manifest, string hostRoot, string? pluginDirectory = null)
    {
        PluginDesktopPolicy.Validate(manifest);
        if (!PluginDesktopPolicy.IsAllowed(manifest)) throw new UnauthorizedAccessException();
        _hostRoot = Path.GetFullPath(hostRoot);
        _pluginDirectory = pluginDirectory is null ? null : Path.GetFullPath(pluginDirectory);
        _allowBridge = manifest.Id == "wini_plugin_telegram" && ScopeForBridge(manifest);
        var scope = manifest.Desktop!;
        _scope = new(scope.Processes.ToArray(), scope.Modules.ToArray(), scope.Purpose);
    }

    private static bool ScopeForBridge(PluginManifest manifest) => manifest.Desktop!.Processes.SequenceEqual(["Telegram"]);

    public bool OwnsProcess(int id) => Volatile.Read(ref _revoked) == 0 && Volatile.Read(ref _processIds).Contains(id);

    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _operations.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _revoked) != 0, this);
            return await Task.Run(() => { linked.Token.ThrowIfCancellationRequested(); return action(); }, linked.Token).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    public Task<DesktopClient[]> FindClientsAsync(CancellationToken token = default) => RunAsync(() =>
    {
        var found = new List<DesktopClient>();
        var alive = new HashSet<int>();
        foreach (var name in _scope.Processes)
        {
            foreach (var process in Process.GetProcessesByName(name).Take(16))
            {
                var retained = false;
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainModule?.FileName is not { } executable
                        || executable.StartsWith(Path.TrimEndingDirectorySeparator(_hostRoot) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(executable).StartsWith("WindowsIsland", StringComparison.OrdinalIgnoreCase)) continue;
                    var startedAt = process.StartTime.ToUniversalTime().Ticks;
                    if (_clients.TryGetValue(process.Id, out var previous) && previous.StartedAt != startedAt) RemoveClient(process.Id);
                    if (!_clients.TryGetValue(process.Id, out var client))
                    {
                        var handle = OpenProcess(0x0438, false, process.Id);
                        if (handle.IsInvalid) { handle.Dispose(); continue; }
                        client = new(process, handle, startedAt);
                        try
                        {
                            foreach (ProcessModule module in process.Modules)
                            {
                                if (!_scope.Modules.Contains(module.ModuleName, StringComparer.OrdinalIgnoreCase)) continue;
                                PluginPaths.CheckLinks(module.FileName);
                                var stream = PluginFileSafety.OpenSingleLinkRead(module.FileName);
                                if (stream.Length is <= 0 or > 512 * 1024 * 1024) { stream.Dispose(); continue; }
                                client.Modules.Add(module.ModuleName, new(module, stream));
                            }
                            client.Icon = ReadIcon(executable);
                            _clients.Add(process.Id, client);
                            retained = true;
                        }
                        catch { DisposeClient(client); throw; }
                    }
                    RefreshModules(client);
                    alive.Add(process.Id);
                    found.Add(new(process.Id, startedAt, executable, process.MainModule?.FileVersionInfo.FileVersion ?? "",
                        client.Modules.Values.Select(module => new DesktopModule(module.Name, module.Address.ToInt64(), module.Size, module.File.Length)).ToArray(),
                        Windows(process.Id), client.Icon));
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException
                    or IOException or UnauthorizedAccessException or ArgumentException) { }
                finally { if (!retained) process.Dispose(); }
            }
        }
        foreach (var id in _clients.Keys.Except(alive).ToArray()) RemoveClient(id);
        Volatile.Write(ref _processIds, alive.ToArray());
        return found.Take(16).ToArray();
    }, token);

    private Client GetClient(int id)
    {
        if (!_clients.TryGetValue(id, out var client) || client.Process.HasExited
            || client.Process.StartTime.ToUniversalTime().Ticks != client.StartedAt) throw new UnauthorizedAccessException();
        return client;
    }

    private void RefreshModules(Client client)
    {
        client.Process.Refresh();
        var modules = client.Process.Modules.Cast<ProcessModule>().Where(module => _scope.Modules.Contains(module.ModuleName, StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (var name in client.Modules.Keys.ToArray())
        {
            var current = modules.FirstOrDefault(module => module.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase));
            var previous = client.Modules[name];
            if (current is not null && current.BaseAddress == previous.Address && current.ModuleMemorySize == previous.Size) continue;
            previous.File.Dispose(); client.Modules.Remove(name);
            foreach (var key in client.Changed.Keys.Where(key => key.Item1.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray()) client.Changed.Remove(key);
        }
        foreach (var module in modules)
        {
            if (client.Modules.ContainsKey(module.ModuleName)) continue;
            PluginPaths.CheckLinks(module.FileName);
            var stream = PluginFileSafety.OpenSingleLinkRead(module.FileName);
            if (stream.Length is <= 0 or > 512 * 1024 * 1024) { stream.Dispose(); continue; }
            client.Modules.Add(module.ModuleName, new(module, stream));
        }
    }

    private (Client Client, Module Module) GetModule(DesktopRead request)
    {
        if (request.Count is <= 0 or > 1024 * 1024 || request.Offset < 0) throw new InvalidDataException();
        var client = GetClient(request.ProcessId);
        if (!client.Modules.TryGetValue(request.Module, out var module)) throw new UnauthorizedAccessException();
        return (client, module);
    }

    public Task<byte[]> ReadModuleAsync(DesktopRead request, CancellationToken token = default) => RunAsync(() =>
    {
        var (_, module) = GetModule(request);
        if (request.Offset > module.File.Length - request.Count) throw new InvalidDataException();
        var bytes = new byte[request.Count];
        module.File.Position = request.Offset;
        module.File.ReadExactly(bytes);
        return bytes;
    }, token);

    public Task<byte[]> ReadMemoryAsync(DesktopRead request, CancellationToken token = default) => RunAsync(() =>
    {
        var (client, module) = GetModule(request);
        return Read(client, module, request.Offset, request.Count);
    }, token);

    private static byte[] Read(Client client, Module module, long offset, int count)
    {
        if (offset < 0 || count is <= 0 or > 1024 * 1024 || offset > module.Size - count) throw new InvalidDataException();
        var bytes = new byte[count];
        if (!ReadProcessMemory(client.Handle, module.Address + checked((int)offset), bytes, (nuint)count, out var read) || read != (nuint)count)
            throw new IOException("Client memory is unavailable.");
        return bytes;
    }

    public Task<bool> SetFlagAsync(DesktopFlag request, CancellationToken token = default) => RunAsync(() =>
    {
        if (request.Value > 1 || request.Anchors is not { Length: >= 1 and <= 8 }
            || request.Anchors.Any(anchor => anchor.Bytes is not { Length: > 0 and <= 65536 })) throw new InvalidDataException();
        var (client, module) = GetModule(new(request.ProcessId, request.Module, request.Offset, 1));
        if (request.Offset >= module.Size || !Matches(client, module, request)) return false;
        var address = module.Address + checked((int)request.Offset);
        if (VirtualQueryEx(client.Handle, address, out var region, (nuint)Marshal.SizeOf<MemoryRegion>()) == 0
            || region.AllocationBase != module.Address || region.State != 0x1000 || region.Type != 0x1000000
            || region.Protect is not (0x04 or 0x08)) return false;
        var current = Read(client, module, request.Offset, 1)[0];
        if (current > 1) return false;
        if (current == request.Value) return true;
        if (Volatile.Read(ref _revoked) != 0 || token.IsCancellationRequested) throw new OperationCanceledException();
        if (!WriteProcessMemory(client.Handle, address, [request.Value], 1, out var written) || written != 1) return false;
        var key = (module.Name, request.Offset);
        if (request.Value == 1) client.Changed.TryAdd(key, request with { Anchors = request.Anchors.Select(a => a with { Bytes = a.Bytes.ToArray() }).ToArray() });
        else client.Changed.Remove(key);
        return true;
    }, token);

    private static bool Matches(Client client, Module module, DesktopFlag request) => request.Anchors.All(anchor =>
        Read(client, module, anchor.Offset, anchor.Bytes.Length).AsSpan().SequenceEqual(anchor.Bytes));

    private void RemoveClient(int id) { CloseBridge(id); if (_clients.Remove(id, out var client)) DisposeClient(client); }
    private static void DisposeClient(Client client)
    {
        try
        {
            var reader = 0;
            if (SystemParametersInfo(0x46, 0, ref reader, 0) && reader == 0)
                foreach (var changed in client.Changed.Values)
                {
                    try
                    {
                        var module = client.Modules[changed.Module];
                        if (Matches(client, module, changed) && Read(client, module, changed.Offset, 1)[0] == 1)
                            WriteProcessMemory(client.Handle, module.Address + checked((int)changed.Offset), [0], 1, out _);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or OverflowException) { }
                }
        }
        finally
        {
            foreach (var module in client.Modules.Values) module.File.Dispose();
            client.Handle.Dispose(); client.Process.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _revoked, 1) != 0) return;
        Volatile.Write(ref _processIds, []);
        _lifetime.Cancel();
        _ = Task.Run(FinishAsync);
    }

    private async Task FinishAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var id in _clients.Keys.ToArray()) RemoveClient(id);
            ClearTrees();
            _disposed.TrySetResult();
        }
        catch (Exception error) { _disposed.TrySetException(error); }
        finally { _operations.Release(); }
    }
    public async ValueTask DisposeAsync() { Dispose(); await _disposed.Task.ConfigureAwait(false); }

    private static DesktopWindow[] Windows(int processId)
    {
        var windows = new List<DesktopWindow>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var id);
            if (id != processId || windows.Count >= 64 || !GetWindowRect(window, out var rect)) return true;
            var text = new StringBuilder(256); GetClassName(window, text, text.Capacity);
            windows.Add(new(window.ToInt64(), text.ToString(), new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
                GetWindowLongPtr(window, -16).ToInt64(), GetWindowLongPtr(window, -20).ToInt64(), GetWindow(window, 4) != 0,
                IsWindowVisible(window), IsIconic(window), window == GetForegroundWindow(), Math.Max(1, GetDpiForWindow(window) / 96d)));
            return true;
        }, 0);
        return windows.ToArray();
    }

    private delegate bool EnumWindowsProc(nint window, nint data);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryRegion
    {
        public nint BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId;
        public nuint RegionSize; public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll")] private static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryRegion region, nuint size);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint parameter, ref int value, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint id);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
}
