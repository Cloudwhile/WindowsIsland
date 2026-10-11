using System.Buffers.Binary;
using System.IO;
using System.Reflection.PortableExecutable;

namespace WindowsIsland.Services;

// Locate the accessibility gate by its reader, writer and notification consumer.
// Only RIP-relative operands may vary; an unknown instruction layout fails closed.
internal static class WeChatAccessibilityLocator
{
    private const int MaximumImageSize = 512 * 1024 * 1024;
    private const int ChunkSize = 1024 * 1024;
    private static readonly byte[] Accessor = Convert.FromHexString(
        "0FB60500000000C30F1F840000000000880D00000000C3");
    private static readonly byte[] Consumer = Convert.FromHexString(
        "4885C90F8400000000803D00000000000F84000000004889CEE8000000004C8B40204183781400");

    internal sealed record Anchor(int Rva, byte[] Bytes);
    internal sealed record Location(int FlagRva, int ImageSize, Anchor Header, Anchor ReaderWriter, Anchor Notification);

    internal static bool MatchesLoadedHeader(Location location, ReadOnlySpan<byte> loaded, long moduleBase)
    {
        var original = location.Header.Bytes.AsSpan();
        if (loaded.Length != original.Length || original.Length < 0x40) return false;
        var offset = (long)BinaryPrimitives.ReadInt32LittleEndian(original[0x3c..]) + 48;
        if (offset < 0 || offset + 8 > original.Length) return false;
        var field = (int)offset;
        var preferred = BinaryPrimitives.ReadInt64LittleEndian(original[field..]);
        var actual = BinaryPrimitives.ReadInt64LittleEndian(loaded[field..]);
        // Some loaded clients rewrite only ImageBase to their mapped address.
        // Accept that exact value, never arbitrary changes or other header edits.
        return (actual == preferred || actual == moduleBase)
            && original[..field].SequenceEqual(loaded[..field])
            && original[(field + 8)..].SequenceEqual(loaded[(field + 8)..]);
    }

    public static Location? Find(Stream stream)
    {
        try { return FindCore(stream); }
        catch (Exception error) when (error is IOException or BadImageFormatException or ArgumentException or OverflowException)
        { return null; }
    }

    private static Location? FindCore(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek || stream.Length > MaximumImageSize) return null;
        stream.Position = 0;
        using var image = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var headers = image.PEHeaders;
        var pe = headers.PEHeader;
        if (headers.CoffHeader.Machine != Machine.Amd64 || pe is null || pe.Magic != PEMagic.PE32Plus
            || pe.SizeOfImage <= 0 || pe.SizeOfImage > MaximumImageSize
            || pe.SizeOfHeaders <= 0 || pe.SizeOfHeaders > 64 * 1024
            || headers.SectionHeaders.Length is 0 or > 96) return null;

        var sections = headers.SectionHeaders;
        foreach (var section in sections)
        {
            if (section.VirtualAddress < pe.SizeOfHeaders || section.VirtualSize <= 0
                || (long)section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData) > pe.SizeOfImage
                || section.SizeOfRawData < 0 || section.PointerToRawData < 0
                || (section.SizeOfRawData > 0 && (section.PointerToRawData < pe.SizeOfHeaders
                    || (long)section.PointerToRawData + section.SizeOfRawData > stream.Length))) return null;
        }
        var ordered = sections.OrderBy(section => section.VirtualAddress).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            if ((long)ordered[i - 1].VirtualAddress + Math.Max(ordered[i - 1].VirtualSize, ordered[i - 1].SizeOfRawData)
                > ordered[i].VirtualAddress) return null;

        var accessors = new List<(int Flag, Anchor Code)>();
        var consumers = new List<(int Flag, Anchor Code)>();
        var buffer = new byte[ChunkSize + Consumer.Length];
        foreach (var section in sections)
        {
            var flags = section.SectionCharacteristics;
            if ((flags & SectionCharacteristics.MemExecute) == 0 || (flags & SectionCharacteristics.MemWrite) != 0) continue;
            var length = Math.Min(section.SizeOfRawData, section.VirtualSize);
            for (var offset = 0; offset < length; offset += ChunkSize)
            {
                var count = Math.Min(buffer.Length, length - offset);
                stream.Position = (long)section.PointerToRawData + offset;
                stream.ReadExactly(buffer.AsSpan(0, count));
                var bytes = buffer.AsSpan(0, count);
                for (var i = 0; i < Math.Min(ChunkSize, count); i++)
                {
                    var rva = section.VirtualAddress + offset + i;
                    if (bytes[i] == 0x0f && Matches(bytes[i..], Accessor, true))
                    {
                        var flag = Relative(bytes[i..], rva, 3, 7);
                        if (flag == Relative(bytes[i..], rva, 18, 22) && IsWritableData(flag))
                            accessors.Add(((int)flag, new(rva, bytes.Slice(i, Accessor.Length).ToArray())));
                    }
                    else if (bytes[i] == 0x48 && Matches(bytes[i..], Consumer, false))
                    {
                        var flag = Relative(bytes[i..], rva, 11, 16);
                        // Both null-object and inactive-gate branches must share their exit.
                        var exit = Relative(bytes[i..], rva, 5, 9);
                        if (flag >= 0 && IsWritableData(flag) && IsExecutable(exit)
                            && IsExecutable(Relative(bytes[i..], rva, 26, 30))
                            && exit == Relative(bytes[i..], rva, 18, 22))
                            consumers.Add(((int)flag, new(rva, bytes.Slice(i, Consumer.Length).ToArray())));
                    }
                    if (accessors.Count > 64 || consumers.Count > 64) return null;
                }
            }
        }
        var matches = accessors.Where(accessor => consumers.Any(consumer => consumer.Flag == accessor.Flag)).ToArray();
        if (matches.Length != 1) return null;
        var match = matches[0];
        var headerBytes = new byte[pe.SizeOfHeaders];
        stream.Position = 0;
        stream.ReadExactly(headerBytes);
        return new(match.Flag, pe.SizeOfImage, new(0, headerBytes), match.Code,
            consumers.First(consumer => consumer.Flag == match.Flag).Code);

        bool IsWritableData(long rva) => sections.Any(section =>
            (section.SectionCharacteristics & (SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.MemExecute))
                == (SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite)
            && rva >= section.VirtualAddress && rva < (long)section.VirtualAddress + section.VirtualSize);
        bool IsExecutable(long rva) => sections.Any(section =>
            (section.SectionCharacteristics & (SectionCharacteristics.MemExecute | SectionCharacteristics.MemWrite))
                == SectionCharacteristics.MemExecute
            && rva >= section.VirtualAddress && rva < (long)section.VirtualAddress + Math.Min(section.VirtualSize, section.SizeOfRawData));
    }

    private static long Relative(ReadOnlySpan<byte> bytes, int rva, int operand, int next) =>
        (long)rva + next + BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(operand, 4));

    private static bool Matches(ReadOnlySpan<byte> bytes, byte[] pattern, bool accessor)
    {
        if (bytes.Length < pattern.Length) return false;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (accessor ? i is >= 3 and < 7 or >= 18 and < 22
                : i is >= 5 and < 9 or >= 11 and < 15 or >= 18 and < 22 or >= 26 and < 30) continue;
            if (bytes[i] != pattern[i]) return false;
        }
        return true;
    }
}
