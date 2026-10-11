using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WindowsIsland.Services;

internal static class AccessibilityChecks
{
    public static int Run()
    {
        var checks = 0;
        foreach (var (code, data, flag) in new[] { (0x1000, 0x3000, 0x21), (0x5000, 0x2000, 0x170), (0x2000, 0x7000, 0x83) })
        {
            var bytes = Fixture(code, data, flag);
            var found = Find(bytes);
            Check(found?.FlagRva == data + flag, "Accessibility discovery follows relocated code and data without version metadata");
            Check(found?.ReaderWriter.Rva == code + 0x40 && found.Notification.Rva == code + 0x100,
                "The gate is corroborated by its reader, writer and notification consumer");
        }
        Reject(bytes => bytes[0x80 + 4] = 0x4c, "Non-x64 images are rejected");
        Reject(bytes => bytes[0x240] = 0x90, "A changed reader instruction is rejected");
        Reject(bytes => Write(bytes, 0x240 + 18, 0), "Reader and writer must target the same gate");
        Reject(bytes => Write(bytes, 0x300 + 11, 0), "The notification consumer must target the same gate");
        Reject(bytes => bytes[0x300] = 0x90, "A missing notification consumer is rejected");
        Reject(bytes => Write(bytes, 0x300 + 18, 0), "Both consumer branches must share their exit");
        Reject(bytes => Write(bytes, 0x300 + 26, int.MaxValue), "Consumer calls must remain inside executable module code");
        Reject(bytes =>
        {
            Relative(bytes, 0x300, 0x1100, 5, 9, 0x3021);
            Relative(bytes, 0x300, 0x1100, 18, 22, 0x3021);
        }, "A shared branch into data is rejected");
        Reject(bytes => Write(bytes, 0x240 + 3, int.MaxValue), "Overflowing relative addresses are rejected");
        Reject(bytes => Write(bytes, 0x1d4, 0x40000040), "Read-only gate sections are rejected");
        Reject(bytes => Write(bytes, 0x1d4, unchecked((int)0xe0000040)), "Executable gate sections are rejected");
        Reject(bytes => Write(bytes, 0x1bc, 0x1000), "Overlapping virtual sections are rejected");
        Reject(bytes => Write(bytes, 0x1c4, int.MaxValue), "Truncated section data is rejected");
        Reject(bytes =>
        {
            bytes.AsSpan(0x240, 23).CopyTo(bytes.AsSpan(0x280));
            Relative(bytes, 0x280, 0x1080, 3, 7, 0x3021);
            Relative(bytes, 0x280, 0x1080, 18, 22, 0x3021);
        }, "Duplicate reader/writer candidates are rejected instead of choosing the first");
        Reject(bytes =>
        {
            AddCode(bytes, 0x280, 0x1080, 0x380, 0x1180, 0x3022);
        }, "Two independently matching gates are ambiguous and rejected");
        Check(Find([0x4d, 0x5a]) is null, "Truncated PE headers fail closed");
        var chunk = Fixture(0x1000, 0x300000, 0x21, 1024 * 1024 + 0x200);
        chunk.AsSpan(0x240, 23).Clear();
        AddCode(chunk, 0x200 + 1024 * 1024 - 8, 0x1000 + 1024 * 1024 - 8, 0x300, 0x1100, 0x300021);
        Check(Find(chunk)?.FlagRva == 0x300021, "Signatures crossing a scan chunk boundary are found once");
        CheckLiveMapping();
        var located = Find(Fixture(0x1000, 0x2000, 0x21))!;
        var loadedHeader = located.Header.Bytes.ToArray();
        const long relocatedBase = 0x7ffd92740000;
        Check(WeChatAccessibilityLocator.MatchesLoadedHeader(located, loadedHeader, relocatedBase),
            "A loaded header may retain its preferred ImageBase after relocation");
        BinaryPrimitives.WriteInt64LittleEndian(loadedHeader.AsSpan(0xb0), relocatedBase);
        Check(WeChatAccessibilityLocator.MatchesLoadedHeader(located, loadedHeader, relocatedBase),
            "A loaded header may rewrite ImageBase to the exact mapped module address");
        Check(!WeChatAccessibilityLocator.MatchesLoadedHeader(located, loadedHeader, relocatedBase + 0x10000),
            "An unrelated ImageBase still fails header validation");
        loadedHeader[0xd0] ^= 1;
        Check(!WeChatAccessibilityLocator.MatchesLoadedHeader(located, loadedHeader, relocatedBase),
            "Relocated ImageBase does not bypass validation of any other header field");
        Console.WriteLine($"{checks} WeChat accessibility compatibility checks passed.");
        return checks;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
            Console.WriteLine("PASS " + message);
        }
        void Reject(Action<byte[]> change, string message)
        {
            var bytes = Fixture(0x1000, 0x3000, 0x21);
            change(bytes);
            Check(Find(bytes) is null, message);
        }
        void CheckLiveMapping()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "accessibility-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "Weixin.dll");
            File.WriteAllBytes(path, Fixture(0x1000, 0x2000, 0x21));
            var module = LoadLibraryEx(path, 0, 1); // Load a synthetic image without running any DLL code.
            try
            {
                Check(module != 0, $"The isolated accessibility image loads without an entry point (error={Marshal.GetLastWin32Error()})");
                using var process = Process.GetCurrentProcess();
                var address = module + 0x2021;
                using (var session = WeChatAccessibilitySession.TryOpen(process))
                {
                    Check(session is not null && Marshal.ReadByte(address) == 1, "Validated loaded code enables only the discovered gate");
                    Marshal.WriteByte(address, 0);
                    Check(session!.EnsureEnabled(out var restored) && restored && Marshal.ReadByte(address) == 1,
                        "A client resetting its accessibility gate is recovered without reopening or rescanning the module");
                    Check(session.EnsureEnabled(out restored) && !restored, "An active accessibility gate does not repeatedly trigger reconnection");
                    Marshal.WriteByte(address, 2);
                    Check(!session.EnsureEnabled(out _) && Marshal.ReadByte(address) == 2,
                        "Recovery refuses unexpected live values instead of overwriting them");
                    Marshal.WriteByte(address, 1);
                }
                var screenReader = 0;
                Check(SystemParametersInfo(0x46, 0, ref screenReader, 0)
                    && Marshal.ReadByte(address) == (screenReader == 0 ? 0 : 1), "Cleanup respects the current screen-reader state");
                Marshal.WriteByte(address, 1);
                using (var session = WeChatAccessibilitySession.TryOpen(process))
                    Check(session is not null, "An already active gate is accepted");
                Check(Marshal.ReadByte(address) == 1, "Cleanup preserves an already active gate");
                Marshal.WriteByte(address, 2);
                using var rejected = WeChatAccessibilitySession.TryOpen(process);
                Check(rejected is null && Marshal.ReadByte(address) == 2, "Unknown live gate values are never overwritten");
                Marshal.WriteByte(address, 0);
                Check(VirtualProtect(address, 1, 0x02, out var dataProtection), "The synthetic gate can simulate a read-only mapping");
                try
                {
                    using var readOnly = WeChatAccessibilitySession.TryOpen(process);
                    Check(readOnly is null && Marshal.ReadByte(address) == 0, "Read-only live mappings are never overwritten");
                }
                finally { VirtualProtect(address, 1, dataProtection, out _); }
                using var pendingRestore = WeChatAccessibilitySession.TryOpen(process);
                Check(pendingRestore is not null && Marshal.ReadByte(address) == 1, "A validated session is established before the unload/change simulation");
                Check(VirtualProtect(module + 0x1040, 1, 0x04, out var protection), "Only the synthetic image is made writable for the mismatch check");
                Marshal.WriteByte(module + 0x1040, 0x90);
                VirtualProtect(module + 0x1040, 1, protection, out _);
                pendingRestore!.Dispose();
                Check(Marshal.ReadByte(address) == 1, "Cleanup does not restore a gate after its module instructions change");
                Marshal.WriteByte(address, 0);
                using var mismatch = WeChatAccessibilitySession.TryOpen(process);
                Check(mismatch is null && Marshal.ReadByte(address) == 0, "Loaded instructions must match the file before enabling accessibility");
            }
            finally
            {
                if (module != 0) FreeLibrary(module);
                File.Delete(path);
                Directory.Delete(directory);
            }
        }
    }

    public static int CheckModule(string path)
    {
        using var stream = File.OpenRead(path);
        var found = WeChatAccessibilityLocator.Find(stream);
        Console.WriteLine(found is null ? "No compatible accessibility gate found." : $"Compatible accessibility gate: RVA 0x{found.FlagRva:X}");
        return found is null ? 1 : 0;
    }

    internal static void WriteFixtureModule(string path) => File.WriteAllBytes(path, Fixture(0x1000, 0x2000, 0x21));

    private static WeChatAccessibilityLocator.Location? Find(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        return WeChatAccessibilityLocator.Find(stream);
    }

    private static byte[] Fixture(int codeRva, int dataRva, int flag, int codeSize = 0x400)
    {
        var bytes = new byte[0x200 + codeSize + 0x200];
        bytes[0] = 0x4d; bytes[1] = 0x5a;
        Write(bytes, 0x3c, 0x80); Write(bytes, 0x80, 0x4550);
        Short(0x84, 0x8664); Short(0x86, 2); Short(0x94, 0xf0); Short(0x96, 0x2022);
        Short(0x98, 0x20b); Write(bytes, 0x9c, codeSize); Write(bytes, 0xa0, 0x200);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0xb0), 0x180000000);
        Write(bytes, 0xb8, 0x1000); Write(bytes, 0xbc, 0x200); Short(0xc0, 6); Short(0xc8, 6);
        Write(bytes, 0xd0, (Math.Max(codeRva + codeSize, dataRva + 0x200) + 0xfff) & ~0xfff);
        Write(bytes, 0xd4, 0x200); Short(0xdc, 3);
        Write(bytes, 0xe0, 0x100000); Write(bytes, 0xe8, 0x1000);
        Write(bytes, 0xf0, 0x100000); Write(bytes, 0xf8, 0x1000); Write(bytes, 0x104, 16);
        Section(0x188, codeRva, codeSize, 0x200, 0x60000020);
        Section(0x1b0, dataRva, 0x200, 0x200 + codeSize, unchecked((int)0xc0000040));
        AddCode(bytes, 0x240, codeRva + 0x40, 0x300, codeRva + 0x100, dataRva + flag);
        return bytes;

        void Short(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        void Section(int offset, int rva, int size, int raw, int flags)
        {
            Write(bytes, offset + 8, size); Write(bytes, offset + 12, rva);
            Write(bytes, offset + 16, size); Write(bytes, offset + 20, raw); Write(bytes, offset + 36, flags);
        }
    }

    private static void AddCode(byte[] bytes, int accessorOffset, int accessorRva, int consumerOffset, int consumerRva, int flag)
    {
        Convert.FromHexString("0FB60500000000C30F1F840000000000880D00000000C3").CopyTo(bytes, accessorOffset);
        Relative(bytes, accessorOffset, accessorRva, 3, 7, flag);
        Relative(bytes, accessorOffset, accessorRva, 18, 22, flag);
        Convert.FromHexString("4885C90F8400000000803D00000000000F84000000004889CEE8000000004C8B40204183781400").CopyTo(bytes, consumerOffset);
        Relative(bytes, consumerOffset, consumerRva, 5, 9, consumerRva + 0x80);
        Relative(bytes, consumerOffset, consumerRva, 11, 16, flag);
        Relative(bytes, consumerOffset, consumerRva, 18, 22, consumerRva + 0x80);
    }

    private static void Relative(byte[] bytes, int offset, int rva, int operand, int next, int target) =>
        Write(bytes, offset + operand, target - rva - next);
    private static void Write(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint parameter, ref int value, uint flags);
}
