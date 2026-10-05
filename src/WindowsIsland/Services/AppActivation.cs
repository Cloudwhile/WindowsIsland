using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

internal static class AppActivation
{
    public static readonly uint SettingsMessage = RegisterWindowMessage("WindowsIsland.OpenSettings");

    public static void ShowSettings() => PostMessage((nint)0xFFFF, SettingsMessage, 0, 0);

    public static void LaunchRegistered(string appId, bool showSettings = true, bool showUpdateResult = false) =>
        Launch(appId, (showSettings ? "--settings" : "") + (showUpdateResult ? " --show-update-result" : ""));

    public static void Launch(string appId, string arguments)
    {
        var manager = (IApplicationActivationManager)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"))!)!;
        try { Marshal.ThrowExceptionForHR(manager.ActivateApplication(appId, arguments, 2, out _)); }
        finally { Marshal.FinalReleaseComObject(manager); }
    }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
