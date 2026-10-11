using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsIsland.Services;

internal enum WeChatAccessibilityFailure
{
    None, ModuleNotFound, UnsupportedImage, ImageSizeMismatch, OpenProcess,
    MemoryLayout, HeaderMismatch, AccessorMismatch, ConsumerMismatch, ReadFlag, InvalidFlag, WriteFlag
}

internal sealed class WeChatAccessibilitySession : IDisposable
{
    private readonly SafeProcessHandle _process;
    private readonly nint _address;
    private readonly nint _moduleBase;
    private readonly WeChatAccessibilityLocator.Location _location;
    private bool _changed;
    private int _disposed;

    private WeChatAccessibilitySession(SafeProcessHandle process, nint moduleBase,
        WeChatAccessibilityLocator.Location location, bool changed)
    {
        _process = process;
        _moduleBase = moduleBase;
        _location = location;
        _address = moduleBase + location.FlagRva;
        _changed = changed;
    }

    public static WeChatAccessibilitySession? TryOpen(Process process) => TryOpen(process, out _);

    public static WeChatAccessibilitySession? TryOpen(Process process, out WeChatAccessibilityFailure failure)
    {
        failure = WeChatAccessibilityFailure.ModuleNotFound;
        ProcessModule? module = null;
        foreach (ProcessModule item in process.Modules)
            if (item.ModuleName.Equals("Weixin.dll", StringComparison.OrdinalIgnoreCase)) { module = item; break; }
        if (module is null) return null;
        failure = WeChatAccessibilityFailure.UnsupportedImage;
        using var stream = File.OpenRead(module.FileName);
        var location = WeChatAccessibilityLocator.Find(stream);
        if (location is null) return null;
        failure = WeChatAccessibilityFailure.ImageSizeMismatch;
        if (module.ModuleMemorySize != location.ImageSize) return null;

        failure = WeChatAccessibilityFailure.OpenProcess;
        var handle = OpenProcess(0x0438, false, process.Id);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        try
        {
            var address = module.BaseAddress + location.FlagRva;
            var original = new byte[1];
            if (!MatchesLoadedModule(handle, module.BaseAddress, location, out failure)) return null;
            failure = WeChatAccessibilityFailure.ReadFlag;
            if (!ReadProcessMemory(handle, address, original, 1, out var read) || read != 1) return null;
            failure = WeChatAccessibilityFailure.InvalidFlag;
            if (original[0] > 1) return null;
            failure = WeChatAccessibilityFailure.WriteFlag;
            if (original[0] == 0 && (!WriteProcessMemory(handle, address, [1], 1, out var written) || written != 1))
                return null;
            var session = new WeChatAccessibilitySession(handle, module.BaseAddress, location, original[0] == 0);
            failure = WeChatAccessibilityFailure.None;
            handle = null!; // Ownership transfers to the session only after successful validation.
            return session;
        }
        finally { handle?.Dispose(); }
    }

    private static bool MatchesLoadedModule(SafeProcessHandle process, nint moduleBase, WeChatAccessibilityLocator.Location location) =>
        MatchesLoadedModule(process, moduleBase, location, out _);

    private static bool MatchesLoadedModule(SafeProcessHandle process, nint moduleBase, WeChatAccessibilityLocator.Location location,
        out WeChatAccessibilityFailure failure)
    {
        failure = WeChatAccessibilityFailure.MemoryLayout;
        if (VirtualQueryEx(process, moduleBase + location.FlagRva, out var region, (nuint)Marshal.SizeOf<MemoryRegion>()) == 0
            || region.AllocationBase != moduleBase || region.State != 0x1000 || region.Type != 0x1000000
            || region.Protect is not (0x04 or 0x08)) return false;
        foreach (var anchor in new[] { location.Header, location.ReaderWriter, location.Notification })
        {
            failure = ReferenceEquals(anchor, location.Header) ? WeChatAccessibilityFailure.HeaderMismatch
                : ReferenceEquals(anchor, location.ReaderWriter) ? WeChatAccessibilityFailure.AccessorMismatch : WeChatAccessibilityFailure.ConsumerMismatch;
            var bytes = new byte[anchor.Bytes.Length];
            if (!ReadProcessMemory(process, moduleBase + anchor.Rva, bytes, (nuint)bytes.Length, out var read)
                || read != (nuint)bytes.Length) return false;
            if (ReferenceEquals(anchor, location.Header)
                ? !WeChatAccessibilityLocator.MatchesLoadedHeader(location, bytes, moduleBase.ToInt64())
                : !bytes.AsSpan().SequenceEqual(anchor.Bytes)) return false;
        }
        failure = WeChatAccessibilityFailure.None;
        return true;
    }

    public bool EnsureEnabled(out bool restored)
    {
        restored = false;
        if (Volatile.Read(ref _disposed) != 0 || !MatchesLoadedModule(_process, _moduleBase, _location)) return false;
        var current = new byte[1];
        if (!ReadProcessMemory(_process, _address, current, 1, out var read) || read != 1 || current[0] > 1) return false;
        if (current[0] == 1) return true;
        if (!WriteProcessMemory(_process, _address, [1], 1, out var written) || written != 1) return false;
        _changed = restored = true;
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_changed && !_process.IsInvalid && !_process.IsClosed)
        {
            var screenReader = 0;
            var current = new byte[1];
            if (SystemParametersInfo(0x46, 0, ref screenReader, 0) && screenReader == 0
                && MatchesLoadedModule(_process, _moduleBase, _location)
                && ReadProcessMemory(_process, _address, current, 1, out var read) && read == 1 && current[0] == 1)
                WriteProcessMemory(_process, _address, [0], 1, out _);
        }
        _process.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryRegion region, nuint size);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint parameter, ref int value, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRegion
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
