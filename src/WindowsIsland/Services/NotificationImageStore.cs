using System.Runtime.InteropServices;
using System.Text;

namespace WindowsIsland.Services;

internal static class NotificationImageStore
{
    public static ToastImageReference? Read(IslandNotification notification, string? databasePath = null)
        => ReadAsync(notification, databasePath).GetAwaiter().GetResult();

    public static async Task<ToastImageReference?> ReadAsync(IslandNotification notification, string? databasePath = null,
        Func<int, Task>? delay = null)
    {
        if (string.IsNullOrWhiteSpace(notification.AppId)) return null;
        databasePath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Notifications", "wpndatabase.db");
        delay ??= milliseconds => Task.Delay(milliseconds);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (image, retry) = ReadOnce(notification, databasePath);
            if (!retry || attempt == 2) return image;
            await delay(attempt == 0 ? 50 : 100).ConfigureAwait(false);
        }
        return null;
    }

    private static bool Transient(int result) => (result & 0xFF) is 5 or 6;

    private static (ToastImageReference? Image, bool Retry) ReadOnce(IslandNotification notification, string databasePath)
    {
        nint database = 0, statement = 0;
        try
        {
            if (!File.Exists(databasePath)) return (null, false);
            var result = sqlite3_open_v2(databasePath, out database, 0x00040001, 0);
            if (result != 0) return (null, Transient(result));
            sqlite3_busy_timeout(database, 25);
            // This optional cache lookup is read-only and restricted to the notification already granted by the listener.
            const string query = "SELECT n.Payload, n.ArrivalTime FROM Notification n "
                + "JOIN NotificationHandler h ON h.RecordId = n.HandlerId "
                + "WHERE n.Id = ? AND h.PrimaryId = ? COLLATE NOCASE AND n.Type = 'toast' AND n.PayloadType = 'Xml'";
            result = sqlite3_prepare_v2(database, query, -1, out statement, out _);
            if (result != 0) return (null, Transient(result));
            if (sqlite3_bind_int64(statement, 1, notification.Id) != 0
                || sqlite3_bind_text(statement, 2, notification.AppId!, -1, (nint)(-1)) != 0) return (null, false);
            result = sqlite3_step(statement);
            if (result != 100) return (null, result == 101 || Transient(result));
            var arrived = DateTimeOffset.FromFileTime(sqlite3_column_int64(statement, 1));
            if (Math.Abs((arrived - notification.CreatedAt).TotalSeconds) > 1) return (null, false);
            var length = sqlite3_column_bytes(statement, 0);
            var source = sqlite3_column_blob(statement, 0);
            if (length is <= 0 or > 262144 || source == 0) return (null, false);
            var payload = new byte[length];
            Marshal.Copy(source, payload, 0, length);
            var reference = ToastImageReference.Parse(Encoding.UTF8.GetString(payload).TrimStart('\uFEFF'));
            return (reference is null ? null : ToastImageReference.Match(notification, [reference]), false);
        }
        catch (Exception) { return (null, false); }
        finally
        {
            if (statement != 0) sqlite3_finalize(statement);
            if (database != 0) sqlite3_close(database);
        }
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint database, int flags, nint vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(nint database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(nint database, int milliseconds);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string query, int length, out nint statement, out nint tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int64(nint statement, int index, long value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(nint statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, nint destructor);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(nint statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern nint sqlite3_column_blob(nint statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(nint statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(nint statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(nint statement);
}
