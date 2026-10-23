using System.Buffers.Binary;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WindowsIsland.Services;

internal sealed record FileDialogRequest(bool Save, string FileType, string? FileName, long ParentWindow,
    int X, int Y, int Width, int Height);
internal sealed record FileDialogReply(string Kind, long Window = 0, string? Path = null, int Error = 0);

internal static class FileDialogProtocol
{
    private const int MaximumBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length is < 1 or > MaximumBytes) throw new IOException("Invalid file dialog packet size.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumBytes) throw new IOException("Invalid file dialog packet size.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new IOException("Invalid file dialog packet.");
    }
}
