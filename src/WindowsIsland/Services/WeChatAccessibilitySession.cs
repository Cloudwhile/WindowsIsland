using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WindowsIsland.Services;

internal sealed class WeChatAccessibilitySession : IDisposable
{
    private const int ActiveOffset = 0x0B135C38;
    private const string ModuleHash = "10F8E995453E2DA46D4F2B5080CD6DA1F13CC5147746ADC119CEAE38CB039DE5";
    private readonly SafeProcessHandle _process;
    private readonly nint _address;
    private readonly bool _changed;
    private int _disposed;

    private WeChatAccessibilitySession(SafeProcessHandle process, nint address, bool changed)
    {
        _process = process;
        _address = address;
        _changed = changed;
    }

    public static WeChatAccessibilitySession? TryOpen(Process process)
    {
        ProcessModule? module = null;
        foreach (ProcessModule item in process.Modules)
            if (item.ModuleName.Equals("Weixin.dll", StringComparison.OrdinalIgnoreCase)) { module = item; break; }
        if (module is null || module.FileVersionInfo.FileVersion != "4.1.15.13"
            || module.ModuleMemorySize <= ActiveOffset || !MatchesModule(module.FileName)) return null;

        var handle = OpenProcess(0x0438, false, process.Id);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        var address = module.BaseAddress + ActiveOffset;
        var original = new byte[1];
        if (!ReadProcessMemory(handle, address, original, 1, out var read) || read != 1 || original[0] > 1)
        {
            handle.Dispose();
            return null;
        }
        if (original[0] == 0 && (!WriteProcessMemory(handle, address, [1], 1, out var written) || written != 1))
        {
            handle.Dispose();
            return null;
        }
        return new(handle, address, original[0] == 0);
    }

    private static bool MatchesModule(string path)
    {
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(ModuleHash, StringComparison.Ordinal)) return false;
        stream.Position = 0;
        using var image = new PEReader(stream);
        return image.PEHeaders.CoffHeader.Machine == Machine.Amd64
            && image.PEHeaders.SectionHeaders.Any(section =>
                (section.SectionCharacteristics & SectionCharacteristics.MemWrite) != 0
                && ActiveOffset >= section.VirtualAddress && ActiveOffset < section.VirtualAddress + section.VirtualSize);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_changed && !_process.IsInvalid && !_process.IsClosed)
        {
            var screenReader = 0;
            var current = new byte[1];
            if (SystemParametersInfo(0x46, 0, ref screenReader, 0) && screenReader == 0
                && ReadProcessMemory(_process, _address, current, 1, out var read) && read == 1 && current[0] == 1)
                WriteProcessMemory(_process, _address, [0], 1, out _);
        }
        _process.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint written);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint parameter, ref int value, uint flags);
}
