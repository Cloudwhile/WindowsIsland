using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace WindowsIsland.Services;

internal static class InitializationInstaller
{
    internal static bool TryHandle(string[] arguments, out int result)
    {
        result = 0;
        if (arguments.Length == 0 || arguments[0] is not ("--install-initialization" or "--prepare-installed-initialization")) return false;
        result = 2;
        if (arguments.Length != 1 || !OperatingSystem.IsWindows()) return true;
        try
        {
            if (arguments[0] == "--prepare-installed-initialization")
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return true;
                var manifest = Path.Combine(AppContext.BaseDirectory, "AppxManifest.xml");
                Version? previous = null;
                if (File.Exists(manifest))
                {
                    var document = XDocument.Load(manifest);
                    Version.TryParse((string?)document.Root?.Element(document.Root.Name.Namespace + "Identity")?.Attribute("Version"), out previous);
                }
                InitializationPreparation.Prepare(AppContext.BaseDirectory, previous);
            }
            else
                AppInitialization.InitializeAsync(force: true, allowElevation: false).GetAwaiter().GetResult();
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
