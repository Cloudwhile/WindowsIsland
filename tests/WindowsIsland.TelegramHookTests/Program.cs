using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using WindowsIsland.Services;

var workspace = Path.GetFullPath(args.FirstOrDefault() ?? Environment.CurrentDirectory);
var fixture = Path.GetFullPath(Path.Combine(workspace, "artifacts/native/Release/WindowsIsland.TelegramFixture.exe"));
var dll = Path.Combine(workspace, "artifacts/native/Release/WindowsIsland.TelegramHook.dll");
var hookRoot = Path.Combine(workspace, "artifacts", $"telegram-hook-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(hookRoot);
var isolatedDll = Path.Combine(hookRoot, "WindowsIsland.TelegramHook.dll");
File.Copy(dll, isolatedDll);
void Check(bool result, string text) { if (!result) throw new InvalidOperationException(text); Console.WriteLine("PASS: " + text); }
bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }

IntegrityChecks.Run(dll, hookRoot, Check);
if (args.Length > 1) IntegrityChecks.VerifyHost(args[1], dll, Check);

var header = new byte[TelegramHookProtocol.HeaderSize];
BinaryPrimitives.WriteUInt32LittleEndian(header, TelegramHookProtocol.Magic);
BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), TelegramHookProtocol.Version);
BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);
BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 123);
BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), 2);
BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 2);
var packet = TelegramHookProtocol.ReadHeader(header, 123);
Check(TelegramHookProtocol.ReadText(packet, Encoding.Unicode.GetBytes("中文🚀")) == ("中文", "🚀"), "UTF-16 preserves Chinese and surrogate pairs without OCR");
Check(Reject(() => TelegramHookProtocol.ReadHeader(header, 456)), "Messages from another process are rejected");
BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), uint.MaxValue);
Check(Reject(() => TelegramHookProtocol.ReadHeader(header, 123)), "Unbounded payload lengths are rejected before allocation");
Check(Reject(() => TelegramHookProtocol.ReadText(packet, [])), "Truncated payloads are rejected");

if (args.Contains("--live"))
{
    await LiveCapture.RunAsync(isolatedDll);
    return;
}

using var child = Process.Start(new ProcessStartInfo(fixture) { UseShellExecute = false, CreateNoWindow = true })!;
var received = new ConcurrentQueue<IslandNotification>();
try
{
    var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var session = new TelegramHookSession(child.Id, notification =>
    {
        received.Enqueue(notification);
        if (received.Count >= 2) first.TrySetResult();
    }))
    {
        await session.AttachAsync(isolatedDll, fixture: true);
        Check(session.IsConnected, "The DLL loads in the separate test process and completes the pipe handshake");
        child.Refresh();
        var loadedModule = child.Modules.Cast<ProcessModule>().Single(module =>
            module.ModuleName.Equals("WindowsIsland.TelegramHook.dll", StringComparison.OrdinalIgnoreCase));
        Check(Path.GetFullPath(loadedModule.FileName).Equals(Path.GetFullPath(isolatedDll), StringComparison.OrdinalIgnoreCase),
            "The fixture loads the Telegram hook from the supplied installation path");
        Check(!Directory.Exists(Path.Combine(hookRoot, "Hooks")),
            "Attaching the Telegram hook does not create a LocalAppData Hooks cache");
        await first.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var messages = received.ToArray();
        Check(messages.All(message => message.Title == "DLL 消息测试" && message.Body == "你好，Telegram 原始正文。🚀"),
            "The hook reads the original body before its draw cache is cleared and pairs it with the completed title");
        Check(messages.All(message => message.SenderAvatar is { Length: > 0 }), "The native avatar buffer reaches the display as PNG");
        using (var avatar = new System.Drawing.Bitmap(new MemoryStream(messages[0].SenderAvatar!)))
            Check(avatar.Width == 64 && avatar.Height == 64 && avatar.GetPixel(32, 32).ToArgb() == unchecked((int)0xffff9933),
                "Avatar extraction copies only the photo region with the correct color channels");
        Check(messages.Select(message => message.EventId).Distinct().Count() == messages.Length,
            "Separate messages with identical text retain separate event identities");
        Check(messages.Length == 2, "Extra draw calls do not masquerade as new message events");
        Check(messages.All(message => message.AppId == "telegram" && message.Source == NotificationSource.ClientHook), "DLL messages preserve the system-notification merge identity");
    }
    var deadline = Stopwatch.StartNew();
    bool Loaded()
    {
        child.Refresh();
        return child.Modules.Cast<ProcessModule>().Any(module => module.ModuleName == "WindowsIsland.TelegramHook.dll");
    }
    while (Loaded() && deadline.Elapsed.TotalSeconds < 4) await Task.Delay(50);
    Check(!Loaded() && !child.HasExited, "Disconnect removes both detours and unloads the DLL while the target stays alive");
    var renamedDll = Path.Combine(hookRoot, "WindowsIsland.TelegramHook.renamed.dll");
    File.Move(isolatedDll, renamedDll);
    Check(File.Exists(renamedDll), "The installation DLL can be renamed after the bridge disconnects");
    var resumed = new TaskCompletionSource<IslandNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var session = new TelegramHookSession(child.Id, notification => resumed.TrySetResult(notification)))
    {
        await session.AttachAsync(renamedDll, fixture: true);
        Check((await resumed.Task.WaitAsync(TimeSpan.FromSeconds(3))).Body == "你好，Telegram 原始正文。🚀",
            "The same client process can reattach after a tray-listener restart");
    }
    deadline.Restart();
    while (Loaded() && deadline.Elapsed.TotalSeconds < 4) await Task.Delay(50);
    Check(!Loaded() && !child.HasExited, "The reattached bridge also releases its installation DLL");
    for (var attempt = 0; attempt < 80; attempt++)
    {
        try { File.Delete(renamedDll); break; }
        catch (IOException) when (attempt < 79) { await Task.Delay(50); }
        catch (UnauthorizedAccessException) when (attempt < 79) { await Task.Delay(50); }
    }
    Check(!File.Exists(renamedDll), "The Telegram hook DLL can be deleted after disconnect");
}
finally
{
    if (!child.HasExited)
    {
        if (!string.Equals(child.MainModule?.FileName, fixture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected test process path.");
        child.Kill();
        await child.WaitForExitAsync();
    }
    try { if (Directory.Exists(hookRoot)) Directory.Delete(hookRoot, true); }
    catch { }
}
