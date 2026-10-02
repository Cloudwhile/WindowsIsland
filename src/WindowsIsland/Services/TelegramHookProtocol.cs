using System.Buffers.Binary;
using System.Text;

namespace WindowsIsland.Services;

internal sealed record TelegramHookPacket(uint Kind, uint ProcessId, ulong ObjectId, ulong Sequence, long FileTime,
    int TitleLength, int BodyLength, int AvatarLength)
{
    public int TextLength => (TitleLength + BodyLength) * sizeof(char);
    public int PayloadLength => TextLength + AvatarLength;
}

internal static class TelegramHookProtocol
{
    public const uint Magic = 0x57495447;
    public const int Version = 2, HeaderSize = 56;

    public static TelegramHookPacket ReadHeader(ReadOnlySpan<byte> bytes, int expectedProcessId)
    {
        if (bytes.Length != HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != Version) throw new InvalidDataException("Invalid Telegram bridge header.");
        var kind = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var processId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var titleLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]);
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[44..]);
        var avatarLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[48..]);
        if (kind > 2 || processId != expectedProcessId || titleLength > 1024 || bodyLength > 4096
            || avatarLength != 0 && avatarLength != 64 * 64 * 4 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[52..]) != 0
            || kind != 1 && (titleLength != 0 || bodyLength != 0 || avatarLength != 0)) throw new InvalidDataException("Invalid Telegram bridge payload.");
        return new(kind, processId, BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]),
            (int)titleLength, (int)bodyLength, (int)avatarLength);
    }

    public static (string Title, string Body) ReadText(TelegramHookPacket packet, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != packet.PayloadLength) throw new InvalidDataException("Incomplete Telegram message.");
        var utf16 = new UnicodeEncoding(false, false, true);
        return (utf16.GetString(payload[..(packet.TitleLength * 2)]), utf16.GetString(payload[(packet.TitleLength * 2)..packet.TextLength]));
    }
}
