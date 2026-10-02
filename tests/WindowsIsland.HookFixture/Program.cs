using System.Runtime.InteropServices;

if (args.Length < 3) throw new ArgumentException("Expected title, body and readiness path.");
var window = CreateWindowEx(0x88, "STATIC", "Local notification", 0x80000000, 40, 500, 380, 130, 0, 0, 0, 0);
if (window == 0) throw new InvalidOperationException("Could not create fixture window.");
CreateWindowEx(0, "STATIC", args[0], 0x50000000, 20, 16, 330, 24, window, 0, 0, 0);
var changing = args.Length > 3 && args[3] == "--update";
var bodyWindow = CreateWindowEx(0, "STATIC", changing ? "Updating popup text" : args[1],
    0x50000000, 20, 50, 330, 56, window, 0, 0, 0);
File.WriteAllText(args[2], window.ToString());
// Give the app time to attach the same hooks used for real clients.
Thread.Sleep(6500);
ShowWindow(window, 4);
var shownAt = Environment.TickCount64;
var revision = 0;
var completed = !changing;
var deadline = Environment.TickCount64 + 10000;
while (Environment.TickCount64 < deadline)
{
    var elapsed = Environment.TickCount64 - shownAt;
    if (!completed && elapsed >= 500)
    {
        SetWindowText(bodyWindow, args[1]);
        NotifyWinEvent(0x800C, bodyWindow, -4, 0);
        completed = true;
    }
    else if (!completed && elapsed / 40 > revision)
    {
        revision = (int)(elapsed / 40);
        SetWindowText(bodyWindow, $"Updating popup text {revision}");
        NotifyWinEvent(0x800C, bodyWindow, -4, 0);
    }
    while (PeekMessage(out var message, 0, 0, 0, 1))
    {
        TranslateMessage(ref message);
        DispatchMessage(ref message);
    }
    Thread.Sleep(5);
}
DestroyWindow(window);
return 0;

[DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height,
    nint parent, nint menu, nint instance, nint parameter);
[DllImport("user32.dll")] static extern bool ShowWindow(nint window, int command);
[DllImport("user32.dll")] static extern bool DestroyWindow(nint window);
[DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)] static extern bool SetWindowText(nint window, string text);
[DllImport("user32.dll")] static extern void NotifyWinEvent(uint eventId, nint window, int objectId, int childId);
[DllImport("user32.dll")] static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint flags);
[DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
[DllImport("user32.dll", EntryPoint = "DispatchMessageW")] static extern nint DispatchMessage(ref Message message);
[StructLayout(LayoutKind.Sequential)] struct Message
{
    public nint Window;
    public uint Id;
    public nuint WParam;
    public nint LParam;
    public uint Time;
    public int X, Y;
    public uint Private;
}
