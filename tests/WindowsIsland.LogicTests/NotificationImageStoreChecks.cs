using System.Runtime.InteropServices;
using System.Text;
using WindowsIsland.Services;

internal static class NotificationImageStoreChecks
{
    public static void Run(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"WindowsIsland.Images.{Guid.NewGuid():N}.db");
        nint database = 0;
        try
        {
            if (sqlite3_open_v2(path, out database, 6, 0) != 0) throw new InvalidOperationException("Image fixture could not be created");
            var created = DateTimeOffset.UtcNow;
            Execute(database, "CREATE TABLE NotificationHandler(RecordId INTEGER PRIMARY KEY, PrimaryId TEXT); "
                + "CREATE TABLE Notification(Id INTEGER, HandlerId INTEGER, Type TEXT, PayloadType TEXT, ArrivalTime INTEGER, Payload BLOB); "
                + "INSERT INTO NotificationHandler VALUES(1, 'QQ');");
            var first = "<toast><visual><binding template='ToastGeneric'><text>小林</text><text>你好</text><image placement='appLogoOverride' src='C:\\first-photo'/></binding></visual></toast>";
            var second = first.Replace("first-photo", "second-photo");
            Execute(database, $"INSERT INTO Notification VALUES(10, 1, 'toast', 'Xml', {created.ToFileTime()}, X'{Convert.ToHexString(Encoding.UTF8.GetBytes(first))}'); "
                + $"INSERT INTO Notification VALUES(11, 1, 'toast', 'Xml', {created.ToFileTime()}, X'{Convert.ToHexString(Encoding.UTF8.GetBytes(second))}');");
            sqlite3_close(database);
            database = 0;
            var original = File.ReadAllBytes(path);
            var notification = new IslandNotification(10, created, "QQ", "小林", "你好", AppId: "QQ");
            check(NotificationImageStore.Read(notification, path)?.Avatar == "C:\\first-photo"
                && NotificationImageStore.Read(notification with { Id = 11 }, path)?.Avatar == "C:\\second-photo",
                "Read-only notification cache uses exact IDs when repeated messages have different photos");
            check(NotificationImageStore.Read(notification with { AppId = "another-app" }, path) is null,
                "Cached notification images are restricted to their application identity");
            check(NotificationImageStore.Read(notification with { CreatedAt = created.AddMinutes(1) }, path) is null,
                "A reused notification ID cannot supply an older conversation photo");
            check(NotificationImageStore.Read(notification with { Title = "小红" }, path) is null
                && NotificationImageStore.Read(notification with { Body = "另一条消息" }, path) is null,
                "Cached payload text must also match the notification supplied by Windows");
            check(File.ReadAllBytes(path).SequenceEqual(original), "Image lookup never modifies the notification database");
            var missing = path + ".missing";
            check(NotificationImageStore.Read(notification, missing) is null && !File.Exists(missing),
                "Unavailable notification caches are neither created nor required for delivery");
        }
        finally
        {
            if (database != 0) sqlite3_close(database);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void Execute(nint database, string sql)
    {
        if (sqlite3_exec(database, sql, 0, 0, 0) != 0) throw new InvalidOperationException("Image fixture statement failed");
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint database, int flags, nint vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(nint database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string query, nint callback, nint data, nint error);
}
