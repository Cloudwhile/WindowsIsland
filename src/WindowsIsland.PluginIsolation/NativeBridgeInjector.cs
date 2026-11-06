using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsIsland.PluginIsolation;

public static class NativeBridgeInjector
{
    public static string Attach(int processId, string hookPath, string pipeName, string digest,
        string targetDigest, uint magic, uint version, bool fixture = false, long? expectedStartedAt = null)
    {
        using var dll = NativeBridgeIntegrity.Open(hookPath, digest);
        using var process = Process.GetProcessById(processId);
        var executable = process.MainModule?.FileName ?? throw new InvalidOperationException("Client executable is unavailable.");
        using (var file = File.OpenRead(executable))
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(file)), targetDigest, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Client binary has no verified capture profile.");
        var runtimePath = ResolveFilePath(dll.Handle);
        if (process.Modules.Cast<ProcessModule>().Any(module =>
            module.ModuleName.Equals(Path.GetFileName(hookPath), StringComparison.OrdinalIgnoreCase)
            && !ModulePathMatches(module, runtimePath)))
            throw new InvalidOperationException("Another native bridge is still attached.");
        using var target = OpenProcess(0x043A, false, processId);
        if (target.IsInvalid) throw new Win32Exception();
        if (expectedStartedAt is { } expected)
        {
            if (!GetProcessTimes(target, out var created, out _, out _, out _)) throw new Win32Exception();
            var fileTime = ((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
            if (DateTime.FromFileTimeUtc(fileTime).Ticks != expected) throw new UnauthorizedAccessException();
        }
        if (!IsWow64Process2(target, out var machine, out var nativeMachine) || machine != 0 || nativeMachine != 0x8664)
            throw new NotSupportedException("Native bridge hook requires a matching x64 process.");

        var entryOffset = ExportOffset(runtimePath, "StartHook");
        var remoteModule = FindModule(process, runtimePath);
        var loaded = remoteModule == 0;
        if (loaded)
        {
            RunWithBytes(target, RemoteSystemFunction(process, "LoadLibraryW"), Encoding.Unicode.GetBytes(runtimePath + "\0"));
            remoteModule = FindModule(process, runtimePath);
            if (remoteModule == 0) throw new InvalidOperationException("Native bridge hook DLL did not load.");
        }
        // Read the export RVA as PE data. Never load external native code into the host.
        var start = remoteModule + entryOffset;
        var configuration = new byte[16 + 256 * sizeof(char)];
        BitConverter.GetBytes(magic).CopyTo(configuration, 0);
        BitConverter.GetBytes(version).CopyTo(configuration, 4);
        BitConverter.GetBytes(fixture ? 1u : 0u).CopyTo(configuration, 8);
        BitConverter.GetBytes((uint)Environment.ProcessId).CopyTo(configuration, 12);
        var name = Encoding.Unicode.GetBytes("\\\\.\\pipe\\" + pipeName + "\0");
        if (name.Length > 512) throw new ArgumentException("Bridge pipe name is too long.");
        name.CopyTo(configuration, 16);
        var result = RunWithBytes(target, start, configuration);
        if (result != 0)
        {
            if (loaded && result != 2) RunRemote(target, RemoteSystemFunction(process, "FreeLibrary"), remoteModule);
            throw new InvalidOperationException($"Native bridge initialization failed ({result}).");
        }
        return executable;
    }

    private static int ExportOffset(string path, string name)
    {
        using var stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != System.Reflection.PortableExecutable.Machine.Amd64
            || pe.PEHeaders.PEHeader is not { } header) throw new InvalidDataException();
        var directory = header.ExportTableDirectory;
        var data = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, 40).ToArray();
        uint U(int offset) => BitConverter.ToUInt32(data, offset);
        var count = U(24);
        if (count is 0 or > 4096) throw new InvalidDataException();
        var names = pe.GetSectionData(checked((int)U(32))).GetContent(0, checked((int)count * 4)).ToArray();
        var ordinals = pe.GetSectionData(checked((int)U(36))).GetContent(0, checked((int)count * 2)).ToArray();
        for (var index = 0; index < count; index++)
        {
            var bytes = pe.GetSectionData(BitConverter.ToInt32(names, index * 4)).GetReader();
            var labelBytes = new List<byte>();
            while (true)
            {
                if (bytes.RemainingBytes == 0 || labelBytes.Count >= 256) throw new InvalidDataException();
                var value = bytes.ReadByte();
                if (value == 0) break;
                labelBytes.Add(value);
            }
            var label = Encoding.UTF8.GetString(labelBytes.ToArray());
            if (label != name) continue;
            var ordinal = BitConverter.ToUInt16(ordinals, index * 2);
            if (ordinal >= U(20)) throw new InvalidDataException();
            var offset = BitConverter.ToInt32(pe.GetSectionData(checked((int)U(28))).GetContent(ordinal * 4, 4).ToArray());
            if (offset <= 0 || offset >= header.SizeOfImage
                || offset >= directory.RelativeVirtualAddress && offset < directory.RelativeVirtualAddress + directory.Size)
                throw new InvalidDataException();
            return offset;
        }
        throw new InvalidDataException("Native bridge entry is unavailable.");
    }

    private static nint FindModule(Process process, string path)
    {
        process.Refresh();
        return process.Modules.Cast<ProcessModule>().FirstOrDefault(module => ModulePathMatches(module, path))?.BaseAddress ?? 0;
    }

    private static bool ModulePathMatches(ProcessModule module, string path)
    {
        return NormalizePath(module.FileName).Equals(NormalizePath(path), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static string ResolveFilePath(SafeFileHandle file)
    {
        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(file, path, (uint)path.Capacity, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= path.Capacity) throw new PathTooLongException();
        var resolved = path.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + resolved[8..];
        return resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..] : resolved;
    }

    private static nint RemoteSystemFunction(Process process, string function)
    {
        var localKernel = GetModuleHandle("kernel32.dll");
        var address = GetProcAddress(localKernel, function);
        if (address == 0 || !GetModuleHandleEx(4 | 2, address, out var owner)) throw new Win32Exception();
        var path = new StringBuilder(1024);
        GetModuleFileName(owner, path, path.Capacity);
        var remote = process.Modules.Cast<ProcessModule>().First(module => module.ModuleName.Equals(Path.GetFileName(path.ToString()), StringComparison.OrdinalIgnoreCase));
        return remote.BaseAddress + (address - owner);
    }

    private static uint RunWithBytes(SafeProcessHandle process, nint function, byte[] bytes)
    {
        var buffer = VirtualAllocEx(process, 0, (nuint)bytes.Length, 0x3000, 4);
        if (buffer == 0) throw new Win32Exception();
        var canFree = true;
        try
        {
            if (!WriteProcessMemory(process, buffer, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length) throw new Win32Exception();
            try { return RunRemote(process, function, buffer); }
            catch (TimeoutException) { canFree = false; throw; }
        }
        finally { if (canFree) VirtualFreeEx(process, buffer, 0, 0x8000); }
    }

    private static uint RunRemote(SafeProcessHandle process, nint function, nint parameter)
    {
        using var thread = CreateRemoteThread(process, 0, 0, function, parameter, 0, out _);
        if (thread.IsInvalid) throw new Win32Exception();
        var status = WaitForSingleObject(thread, 5000);
        if (status == 0x102) throw new TimeoutException("Native bridge hook initialization timed out.");
        if (status != 0 || !GetExitCodeThread(thread, out var result)) throw new Win32Exception();
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process,
        out System.Runtime.InteropServices.ComTypes.FILETIME created, out System.Runtime.InteropServices.ComTypes.FILETIME exited,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel, out System.Runtime.InteropServices.ComTypes.FILETIME user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort machine, out ushort nativeMachine);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true)] private static extern bool GetModuleHandleEx(uint flags, nint address, out nint module);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode)] private static extern int GetModuleFileName(nint module, StringBuilder path, int size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] bytes, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle CreateRemoteThread(SafeProcessHandle process, nint attributes, nuint stack, nint start, nint parameter, uint flags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeThread(SafeWaitHandle thread, out uint result);
}
