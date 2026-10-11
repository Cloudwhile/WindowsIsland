using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WindowsIsland.Services;

[SupportedOSPlatform("windows")]
internal sealed class WindowsQuietHoursStore : IQuietHoursStore
{
    // Shell COM contract: only the first two IQuietHoursSettings slots are used.
    // Windows may not provide this interface; callers must expose the Settings fallback.
    // Never edit CloudStore blobs or disable toast delivery to emulate quiet hours.
    public string ReadProfile() => WithSettings(settings => settings.GetUserSelectedProfile());

    public void WriteProfile(string profile) => WithSettings(settings =>
    {
        settings.SetUserSelectedProfile(profile);
        return true;
    });

    private static T WithSettings<T>(Func<IQuietHoursSettings, T> action)
    {
        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        // A caller already in STA can use that apartment; balance only our successful initialization.
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
        try
        {
            var classId = new Guid("f53321fa-34f8-4b7f-b9a3-361877cb94cf");
            var interfaceId = typeof(IQuietHoursSettings).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, IntPtr.Zero, 4, ref interfaceId, out var settings));
            try { return action(settings); }
            finally { Marshal.ReleaseComObject(settings); }
        }
        finally { if (initialized >= 0) CoUninitialize(); }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrency);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IQuietHoursSettings settings);

    [ComImport, Guid("6bff4732-81ec-4ffb-ae67-b6c1bc29631f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IQuietHoursSettings
    {
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetUserSelectedProfile();
        void SetUserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profile);
    }
}
