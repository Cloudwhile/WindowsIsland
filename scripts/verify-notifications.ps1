$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IslandClick {
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct IconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    private delegate bool EnumCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier icon, out Rect rect);
    public static IntPtr FindIsland(uint id) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hwnd, data) => {
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            var title = new System.Text.StringBuilder(256); GetWindowText(hwnd, title, 256);
            if (pid == id && title.ToString() == "Windows Island") { result = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static bool HasTrayIcon(IntPtr hwnd) {
        var icon = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = hwnd, Id = 1 };
        Rect rect; return Shell_NotifyIconGetRect(ref icon, out rect) == 0;
    }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
'@
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
$package = Get-AppxPackage -Name WindowsIsland.Desktop
if (!$package) { throw 'Register Windows Island first.' }
$appId = "$($package.PackageFamilyName)!App"
$app = Get-Process WindowsIsland | Where-Object { $_.Path -like '*\artifacts\publish\WindowsIsland.exe' } | Select-Object -First 1
if (!$app) { throw 'Open the registered Windows Island app first.' }
$islandHandle = [IslandClick]::FindIsland($app.Id)
if ($islandHandle -eq [IntPtr]::Zero) { throw 'Island window was not created' }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($islandHandle)
$notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId)
$group = 'island-smoke'
$tags = @('first', 'burst1', 'burst2')

function Find-Name([string]$name) {
    if (![IslandClick]::IsWindowVisible($islandHandle)) { return $null }
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Assert-NoControls {
    foreach ($type in @([System.Windows.Automation.ControlType]::Button, [System.Windows.Automation.ControlType]::Tab, [System.Windows.Automation.ControlType]::Edit)) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
        if ($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition).Count -ne 0) { throw 'Unexpected interactive control in notification-only island' }
    }
}
function Click-Island {
    $bounds = $root.Current.BoundingRectangle
    $point = New-Object IslandClick+Point
    $point.X = [int]($bounds.X + $bounds.Width / 2)
    $point.Y = [int]($bounds.Y + $bounds.Height / 2)
    [uint32]$owner = 0
    [void][IslandClick]::GetWindowThreadProcessId([IslandClick]::WindowFromPoint($point), [ref]$owner)
    if ($owner -ne $app.Id) { throw 'Island is not under the target click point' }
    $original = New-Object IslandClick+Point
    [void][IslandClick]::GetCursorPos([ref]$original)
    try {
        [void][IslandClick]::SetCursorPos($point.X, $point.Y)
        [IslandClick]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        [IslandClick]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    }
    finally { [void][IslandClick]::SetCursorPos($original.X, $original.Y) }
    Start-Sleep -Milliseconds 500
}
function Assert-Idle {
    if ([IslandClick]::IsWindowVisible($islandHandle)) { throw 'Island is still visible while idle' }
    if (![IslandClick]::HasTrayIcon($islandHandle)) { throw 'Tray icon is missing' }
    $app.Refresh()
    if ($app.HasExited) { throw 'Listener exited when notification was hidden' }
}
function Send-Test([string]$tag) {
    $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
    $xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>Island test $tag</text><text>Local notification verification</text></binding></visual><audio silent='true'/></toast>")
    $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
    $toast.Tag = $tag; $toast.Group = $group
    $toast.ExpirationTime = [DateTimeOffset]::Now.AddMinutes(1)
    $notifier.Show($toast)
}
function Wait-Title([string]$tag) {
    $timeout = [Diagnostics.Stopwatch]::StartNew()
    while ($timeout.Elapsed.TotalSeconds -lt 4) {
        if (Find-Name "Island test $tag") { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Notification did not appear: $tag"
}

try {
    Assert-Idle
    'PASS: startup is tray-only with no visible island'
    Send-Test 'first'
    Wait-Title 'first'
    Assert-NoControls
    Click-Island
    if (!(Find-Name 'Island test first')) { throw 'Clicking interrupted the notification' }
    $output = Join-Path (Split-Path $package.InstallLocation -Parent) 'verification'
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    Start-Sleep -Milliseconds 250
    $bounds = $root.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$bounds.X, [int]$bounds.Y, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $output 'notification.png'))
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
    'PASS: real Windows notification expands the island'
    Start-Sleep -Seconds 6
    Assert-Idle
    if (Find-Name 'Island test first') { throw 'Notification text was not cleared' }
    'PASS: notification disappears and island collapses automatically'

    Send-Test 'burst1'; Wait-Title 'burst1'
    Start-Sleep -Seconds 3
    Send-Test 'burst2'; Wait-Title 'burst2'
    Start-Sleep -Seconds 3
    if (!(Find-Name 'Island test burst2')) { throw 'Older timeout dismissed the newer notification' }
    Start-Sleep -Seconds 3
    Assert-Idle
    'PASS: newer notification resets the five-second timeout'

    Assert-Idle
    'PASS: background process and tray remain alive after notifications disappear'
}
finally {
    foreach ($tag in $tags) {
        [Windows.UI.Notifications.ToastNotificationManager]::History.Remove($tag, $group, $appId)
    }
}
