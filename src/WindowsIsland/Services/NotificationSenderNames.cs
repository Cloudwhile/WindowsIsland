using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace WindowsIsland.Services;

internal static class NotificationSenderNames
{
    public static string Read(string identity)
    {
        try
        {
            var name = AppInfo.GetFromAppUserModelId(identity)?.DisplayInfo?.DisplayName;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch (Exception error) when (IsLookupFailure(error)) { }
        IShellItem? item = null;
        nint display = 0;
        try
        {
            var id = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName("shell:AppsFolder\\" + identity, 0, ref id, out item));
            item.GetDisplayName(0, out display);
            return Marshal.PtrToStringUni(display) is { Length: > 0 } name ? name : identity;
        }
        catch (Exception error) when (IsLookupFailure(error)) { return identity; }
        finally
        {
            if (display != 0) Marshal.FreeCoTaskMem(display);
            if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
    }

    private static bool IsLookupFailure(Exception error) => error is COMException or ArgumentException or InvalidOperationException
        or UnauthorizedAccessException or IOException or NotSupportedException or InvalidCastException;

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, ref Guid handler, ref Guid id, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid id, out IShellItem item);
}
