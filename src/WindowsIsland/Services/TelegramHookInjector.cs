using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsIsland.Services;

internal static class TelegramHookInjector
{
    private const string SupportedHash = "D7680288560339FFE89FFEA10B603C87E35AAB1AD5DB5DD516E62D55FD5DA8DC";

    public static string Attach(int processId, string hookPath, string pipeName, bool fixture = false)
    {
        using var process = Process.GetProcessById(processId);
        var executable = process.MainModule?.FileName ?? throw new InvalidOperationException("Telegram executable is unavailable.");
        if (fixture)
        {
            if (!Path.GetFileName(executable).Equals("WindowsIsland.TelegramFixture.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected hook fixture executable.");
        }
        else
        {
            if (!process.ProcessName.Equals("Telegram", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected Telegram process.");
            using var file = File.OpenRead(executable);
            if (Convert.ToHexString(SHA256.HashData(file)) != SupportedHash) throw new NotSupportedException("Telegram binary has no verified capture profile.");
        }

        using var dll = File.OpenRead(hookPath);
        var hash = Convert.ToHexString(SHA256.HashData(dll));
        var directory = Path.Combine(Path.GetDirectoryName(hookPath)!, "Hooks", hash);
        Directory.CreateDirectory(directory);
        var runtimePath = Path.Combine(directory, "WindowsIsland.TelegramHook.dll");
        if (!File.Exists(runtimePath)) File.Copy(hookPath, runtimePath);
        if (process.Modules.Cast<ProcessModule>().Any(module =>
            module.ModuleName.Equals("WindowsIsland.TelegramHook.dll", StringComparison.OrdinalIgnoreCase)
            && !module.FileName.Equals(runtimePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another Telegram bridge is still attached.");
        using var target = OpenProcess(0x043A, false, processId);
        if (target.IsInvalid) throw new Win32Exception();
        if (!IsWow64Process2(target, out var machine, out var nativeMachine) || machine != 0 || nativeMachine != 0x8664)
            throw new NotSupportedException("Telegram hook requires a matching x64 process.");

        var remoteModule = FindModule(process, runtimePath);
        var loaded = remoteModule == 0;
        if (loaded)
        {
            RunWithBytes(target, RemoteSystemFunction(process, "LoadLibraryW"), Encoding.Unicode.GetBytes(runtimePath + "\0"));
            remoteModule = FindModule(process, runtimePath);
            if (remoteModule == 0) throw new InvalidOperationException("Telegram hook DLL did not load.");
        }
        var local = NativeLibrary.Load(runtimePath);
        try
        {
            var start = remoteModule + (NativeLibrary.GetExport(local, "StartHook") - local);
            var configuration = new byte[16 + 256 * sizeof(char)];
            BitConverter.GetBytes(TelegramHookProtocol.Magic).CopyTo(configuration, 0);
            BitConverter.GetBytes((uint)TelegramHookProtocol.Version).CopyTo(configuration, 4);
            BitConverter.GetBytes(fixture ? 1u : 0u).CopyTo(configuration, 8);
            BitConverter.GetBytes((uint)Environment.ProcessId).CopyTo(configuration, 12);
            var name = Encoding.Unicode.GetBytes("\\\\.\\pipe\\" + pipeName + "\0");
            if (name.Length > 512) throw new ArgumentException("Bridge pipe name is too long.");
            name.CopyTo(configuration, 16);
            var result = RunWithBytes(target, start, configuration);
            if (result != 0)
            {
                if (loaded && result != 2) RunRemote(target, RemoteSystemFunction(process, "FreeLibrary"), remoteModule);
                throw new InvalidOperationException($"Telegram native hook initialization failed ({result}).");
            }
        }
        finally { NativeLibrary.Free(local); }
        return executable;
    }

    private static nint FindModule(Process process, string path)
    {
        process.Refresh();
        return process.Modules.Cast<ProcessModule>().FirstOrDefault(module => module.FileName.Equals(path, StringComparison.OrdinalIgnoreCase))?.BaseAddress ?? 0;
    }

    private static nint RemoteSystemFunction(Process process, string function)
    {
        var localKernel = GetModuleHandle("kernel32.dll");
        var address = GetProcAddress(localKernel, function);
        if (address == 0 || !GetModuleHandleEx(4 | 2, address, out var owner)) throw new Win32Exception();
        var path = new StringBuilder(1024);
        GetModuleFileName(owner, path, path.Capacity);
        var remote = process.Modules.Cast<ProcessModule>().First(module => module.ModuleName.Equals(Path.GetFileName(path.ToString()), StringComparison.OrdinalIgnoreCase));
        return remote.BaseAddress + (address - owner);
    }

    private static uint RunWithBytes(SafeProcessHandle process, nint function, byte[] bytes)
    {
        var buffer = VirtualAllocEx(process, 0, (nuint)bytes.Length, 0x3000, 4);
        if (buffer == 0) throw new Win32Exception();
        var canFree = true;
        try
        {
            if (!WriteProcessMemory(process, buffer, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length) throw new Win32Exception();
            try { return RunRemote(process, function, buffer); }
            catch (TimeoutException) { canFree = false; throw; }
        }
        finally { if (canFree) VirtualFreeEx(process, buffer, 0, 0x8000); }
    }

    private static uint RunRemote(SafeProcessHandle process, nint function, nint parameter)
    {
        using var thread = CreateRemoteThread(process, 0, 0, function, parameter, 0, out _);
        if (thread.IsInvalid) throw new Win32Exception();
        var status = WaitForSingleObject(thread, 5000);
        if (status == 0x102) throw new TimeoutException("Telegram hook initialization timed out.");
        if (status != 0 || !GetExitCodeThread(thread, out var result)) throw new Win32Exception();
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort machine, out ushort nativeMachine);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true)] private static extern bool GetModuleHandleEx(uint flags, nint address, out nint module);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode)] private static extern int GetModuleFileName(nint module, StringBuilder path, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] bytes, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle CreateRemoteThread(SafeProcessHandle process, nint attributes, nuint stack, nint start, nint parameter, uint flags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeThread(SafeWaitHandle thread, out uint result);
}
