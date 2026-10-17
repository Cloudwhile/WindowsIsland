using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

internal static class InitializationInstaller
{
    internal static bool TryHandle(string[] arguments, out int result)
    {
        result = 0;
        if (arguments.Length == 0 || arguments[0] != "--install-initialization") return false;
        result = 2;
        if (arguments.Length != 1 || !OperatingSystem.IsWindows()) return true;
        try
        {
            // Share the application's bounded permission preparation, timeout and
            // original-user registration checks rather than maintaining two paths.
            AppInitialization.InitializeAsync(force: true).GetAwaiter().GetResult();
            result = 0;
        }
        catch (OperationCanceledException) { result = 1223; }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { result = 1223; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Security.SecurityException or System.Xml.XmlException
            or COMException or TimeoutException or Win32Exception)
        { result = 1; }
        return true;
    }
}
