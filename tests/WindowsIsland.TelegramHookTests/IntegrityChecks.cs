using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsIsland.Services;

internal static class IntegrityChecks
{
    public static void VerifyHost(string assemblyPath, string payload, Action<bool, string> check)
    {
        var host = System.Reflection.Assembly.LoadFile(Path.GetFullPath(assemblyPath));
        using var resource = host.GetManifestResourceStream("WindowsIsland.TelegramHook.sha256");
        check(resource is not null, "The actual host assembly contains its build-bound hook digest");
        using var reader = new StreamReader(resource!);
        using var file = File.OpenRead(payload);
        check(string.Equals(reader.ReadToEnd().Trim(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)), StringComparison.OrdinalIgnoreCase),
            "The actual host digest matches the native payload used by injection tests");
        var installed = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!, "WindowsIsland.TelegramHook.dll");
        check(File.ReadAllBytes(installed).SequenceEqual(File.ReadAllBytes(payload)), "The application output contains the exact verified hook payload");
    }

    public static void Run(string original, string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "integrity");
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, "WindowsIsland.TelegramHook.dll");
        File.Copy(original, dll);
        using (var verified = TelegramHookIntegrity.Open(dll))
        {
            check(!verified.Handle.IsInvalid, "Build-bound hook hash accepts an unchanged copy");
            check(Rejected(() => { using var writer = File.Open(dll, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }),
                "The verified file handle denies writes until injection completes");
            check(Rejected(() => File.Delete(dll)), "The verified file handle denies deletion");
            check(Rejected(() => Directory.Move(folder, folder + "-moved")), "Held ancestor handles prevent directory replacement during injection");
        }
        Directory.Move(folder, folder + "-moved");
        Directory.Move(folder + "-moved", folder);
        check(true, "Directory handles are released after validation disposal");
        using (var stream = File.Open(dll, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = stream.Length - 1;
            var value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 1));
        }
        check(Rejected(() => { using var verified = TelegramHookIntegrity.Open(dll); }), "A one-byte DLL replacement is rejected");
        File.WriteAllText(dll + ".sha256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dll))));
        check(Rejected(() => TelegramHookInjector.Attach(-1, dll, "unused", fixture: true)),
            "A forged sidecar hash cannot bypass verification, including fixture injection, before target access");
        File.Delete(dll);
        check(Rejected(() => { using var verified = TelegramHookIntegrity.Open(dll); }), "A missing hook fails closed");
        File.Copy(original, dll);
        var hardLink = Path.Combine(folder, "hardlink.dll");
        check(CreateHardLink(hardLink, dll, 0), "An isolated hard-link canary is created");
        check(Rejected(() => { using var verified = TelegramHookIntegrity.Open(hardLink); }), "Hook hard links are rejected even with matching bytes");
        File.Delete(hardLink);
        var junction = Path.Combine(root, "hook-junction");
        using (var command = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{folder}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            command.WaitForExit();
            check(command.ExitCode == 0, "An isolated directory-junction canary is created");
        }
        try
        {
            check(Rejected(() => { using var verified = TelegramHookIntegrity.Open(Path.Combine(junction, Path.GetFileName(dll))); }),
                "Reparse-point ancestors are rejected before loading the hook");
        }
        finally { Directory.Delete(junction); }
        using var restored = TelegramHookIntegrity.Open(dll);
        check(!restored.Handle.IsInvalid, "The original payload remains usable after rejected tampering");
    }

    private static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception) { return true; }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string name, string existing, nint security);
}
