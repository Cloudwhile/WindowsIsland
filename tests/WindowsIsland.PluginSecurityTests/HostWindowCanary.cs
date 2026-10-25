using System.Runtime.InteropServices;
using System.Text;

namespace WindowsIsland.PluginSecurityTests;

// A hidden synthetic window. No real desktop window content or clipboard is inspected.
internal sealed class HostWindowCanary : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _threadId;
    public nint Handle => _ready.Task.GetAwaiter().GetResult();
    public HostWindowCanary()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Private UI isolation canary" };
        _thread.Start();
        _ready.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
    }
    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var window = CreateWindowEx(0, "STATIC", "fixed-host-private-window-canary", 0x80000000, 0, 0, 20, 20, 0, 0, 0, 0);
        if (window == 0) { _ready.TrySetException(new InvalidOperationException("Private canary window creation failed.")); return; }
        _ready.TrySetResult(window);
        try { while (GetMessage(out _, 0, 0, 0) > 0) { } }
        finally { DestroyWindow(window); }
    }
    public bool OwnTitleAvailable()
    {
        var text = new StringBuilder(128);
        return GetWindowText(Handle, text, text.Capacity) > 0 && text.ToString() == "fixed-host-private-window-canary";
    }
    public void Dispose() { if (_threadId != 0) PostThreadMessage(_threadId, 0x12, 0, 0); _thread.Join(2000); }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern nint CreateWindowEx(uint extended, string type, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(nint window, StringBuilder text, int size);
}
