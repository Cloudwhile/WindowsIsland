[CmdletBinding()]
param(
    [switch]$Launch,
    [switch]$VerifyExit,
    [switch]$Isolated,
    [switch]$SkipPointer
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IslandClick {
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }
    public static uint LaunchRegistered(string appId, string arguments) {
        var manager = (IApplicationActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
        try {
            uint id;
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(appId, arguments, 2, out id));
            return id;
        } finally { Marshal.FinalReleaseComObject(manager); }
    }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct IconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    private delegate bool EnumCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier icon, out Rect rect);
    public static IntPtr FindWindow(uint id, string name) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hwnd, data) => {
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            var title = new System.Text.StringBuilder(256); GetWindowText(hwnd, title, 256);
            if (pid == id && title.ToString() == name) { result = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static IntPtr FindIsland(uint id) { return FindWindow(id, "Windows Island"); }
    public static IntPtr FindPopup(uint id) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hwnd, data) => {
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            var name = new System.Text.StringBuilder(256); GetClassName(hwnd, name, 256);
            if (pid == id && name.ToString() == "#32768" && IsWindowVisible(hwnd)) { result = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint style, string name, string title, uint flags, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr module, IntPtr data);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
    public static void CaptureFrame(IntPtr destination, int x, int y, int width, int height) {
        var source = GetDC(IntPtr.Zero);
        try {
            if (!BitBlt(destination, 0, 0, width, height, source, x, y, 0x40CC0020))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        } finally { ReleaseDC(IntPtr.Zero, source); }
    }
    public static IntPtr CreateUnderlay(IntPtr overlay) {
        Rect rect; GetWindowRect(overlay, out rect);
        var reference = CreateWindowEx(0x08000080, "STATIC", "WindowsIsland.BackdropVerification", 0x80000006,
            rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (!SetWindowPos(reference, new IntPtr(-1), rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x50))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        UpdateWindow(reference);
        return reference;
    }
    public static uint Pixel(int x, int y) {
        var dc = GetDC(IntPtr.Zero);
        try { return GetPixel(dc, x, y); } finally { ReleaseDC(IntPtr.Zero, dc); }
    }
    public static long HitTest(IntPtr hwnd, Point point) {
        return SendMessage(hwnd, 0x0084, UIntPtr.Zero,
            new IntPtr(unchecked((point.Y << 16) | (point.X & 0xFFFF)))).ToInt64();
    }
    [DllImport("user32.dll", EntryPoint = "GetMenuStringW", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr menu, uint item, System.Text.StringBuilder text, int count, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMenuItemRect(IntPtr hwnd, IntPtr menu, uint item, out Rect rect);
    public static string MenuText(IntPtr popup, uint item) {
        var menu = SendMessage(popup, 0x1E1, UIntPtr.Zero, IntPtr.Zero);
        var text = new System.Text.StringBuilder(256);
        GetMenuString(menu, item, text, 256, 0x400);
        return text.ToString();
    }
    public static Point MenuPoint(IntPtr popup, uint item) {
        var menu = SendMessage(popup, 0x1E1, UIntPtr.Zero, IntPtr.Zero);
        Rect rect;
        if (!GetMenuItemRect(IntPtr.Zero, menu, item, out rect)) { throw new InvalidOperationException("Could not locate tray menu item"); }
        return new Point { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
    }
    public static bool HasTrayIcon(IntPtr hwnd) {
        var icon = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = hwnd, Id = 1 };
        Rect rect; return Shell_NotifyIconGetRect(ref icon, out rect) == 0;
    }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
'@
[void][IslandClick]::SetProcessDpiAwarenessContext([IntPtr](-4))
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
$package = Get-AppxPackage -Name WindowsIsland.Desktop
if (!$package) { throw 'Register Windows Island first.' }
$appId = "$($package.PackageFamilyName)!App"
$app = Get-Process WindowsIsland -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\artifacts\publish\WindowsIsland.exe' } | Select-Object -First 1
if (!$app -and $Launch) {
    if ($Isolated) {
        [void][IslandClick]::LaunchRegistered($appId, '--verify-local')
    } else {
        Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$appId" -WindowStyle Hidden
    }
    $launchTimeout = [Diagnostics.Stopwatch]::StartNew()
    while (!$app -and $launchTimeout.Elapsed.TotalSeconds -lt 10) {
        Start-Sleep -Milliseconds 200
        $app = Get-Process WindowsIsland -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\artifacts\publish\WindowsIsland.exe' } | Select-Object -First 1
    }
    Start-Sleep -Seconds 2
}
if (!$app) { throw 'Open the registered Windows Island app first.' }
$islandHandle = [IslandClick]::FindIsland($app.Id)
if ($islandHandle -eq [IntPtr]::Zero) { throw 'Island window was not created' }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($islandHandle)
$notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId)
$group = 'island-smoke'
$tags = @('first', 'short', 'long', 'title-only', 'closed', 'burst1', 'burst2')

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
function Assert-SurfaceInput {
    $foreground = [IslandClick]::GetForegroundWindow()
    [uint32]$owner = 0
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $bounds = Get-IslandBounds
        $point = New-Object IslandClick+Point
        $point.X = [int]($bounds.X + $bounds.Width / 2)
        $point.Y = [int]($bounds.Y + $bounds.Height / 2)
        [void][IslandClick]::GetWindowThreadProcessId([IslandClick]::WindowFromPoint($point), [ref]$owner)
        if ($owner -eq $app.Id) { break }
        Start-Sleep -Milliseconds 30
    } while ($clock.Elapsed.TotalSeconds -lt 1)
    if ($owner -ne $app.Id) { throw 'The notification surface does not receive pointer input.' }
    Start-Sleep -Milliseconds 500
    if ([IslandClick]::GetForegroundWindow() -ne $foreground) { throw 'Presenting the notification changed foreground focus' }
}
function Assert-Idle {
    if ([IslandClick]::IsWindowVisible($islandHandle)) { throw 'Island is still visible while idle' }
    if (![IslandClick]::HasTrayIcon($islandHandle)) { throw 'Tray icon is missing' }
    $app.Refresh()
    if ($app.HasExited) { throw 'Listener exited when notification was hidden' }
}
function Send-Test([string]$tag, [string]$title = "Island test $tag", [string]$body = 'Local notification verification') {
    $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
    $safeTitle = [System.Security.SecurityElement]::Escape($title)
    $safeBody = [System.Security.SecurityElement]::Escape($body)
    $bodyElement = if ([string]::IsNullOrWhiteSpace($body)) { '' } else { "<text>$safeBody</text>" }
    $xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>$safeTitle</text>$bodyElement</binding></visual><audio silent='true'/></toast>")
    $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
    $toast.Tag = $tag; $toast.Group = $group
    $toast.ExpirationTime = [DateTimeOffset]::Now.AddMinutes(1)
    $notifier.Show($toast)
}
function Wait-Title([string]$tag, [string]$title = "Island test $tag") {
    $timeout = [Diagnostics.Stopwatch]::StartNew()
    while ($timeout.Elapsed.TotalSeconds -lt 4) {
        if (Find-Name $title) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Notification did not appear: $tag"
}
function Save-Island([string]$filename) {
    $output = Join-Path (Split-Path $package.InstallLocation -Parent) 'verification'
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    Start-Sleep -Milliseconds 250
    $bounds = Get-IslandBounds
    $bitmap = New-Object System.Drawing.Bitmap([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $dc = $graphics.GetHdc()
        try { [IslandClick]::CaptureFrame($dc, [int]$bounds.X, [int]$bounds.Y, $bitmap.Width, $bitmap.Height) }
        finally { $graphics.ReleaseHdc($dc) }
        $bitmap.Save((Join-Path $output $filename))
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Invoke-IslandPointer([bool]$right) {
    $bounds = Get-IslandBounds
    $original = New-Object IslandClick+Point
    [void][IslandClick]::GetCursorPos([ref]$original)
    try {
        [void][IslandClick]::SetCursorPos([int]($bounds.X + $bounds.Width / 2), [int]($bounds.Y + $bounds.Height / 2))
        Start-Sleep -Milliseconds 75
        [uint32]$down = if ($right) { 8 } else { 2 }
        [uint32]$up = if ($right) { 16 } else { 4 }
        [IslandClick]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 75
        [IslandClick]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero)
    } finally { [void][IslandClick]::SetCursorPos($original.X, $original.Y) }
}
function Get-IslandBounds {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'IslandSurface')
    $surface = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (!$surface) { throw 'Notification surface is unavailable' }
    return $surface.Current.BoundingRectangle
}
function Assert-Layout([string]$title, [string]$body = '') {
    $bounds = Get-IslandBounds
    $scale = [IslandClick]::GetDpiForWindow($islandHandle) / 96.0
    if ($bounds.Width -gt 416 * $scale + 1 -or $bounds.Height -gt 218 * $scale + 1) { throw "Notification exceeded its fixed maximum: $bounds" }
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NotificationAppIcon')
    $icon = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (!$icon -or $icon.Current.IsOffscreen) { throw 'Source application icon was not displayed' }
    $iconBounds = $icon.Current.BoundingRectangle
    foreach ($name in @($title, $body) | Where-Object { $_ }) {
        $element = Find-Name $name
        if (!$element -or $element.Current.IsOffscreen) { throw "Notification content is not visible: $name" }
        $textBounds = $element.Current.BoundingRectangle
        if ($textBounds.Left -lt $iconBounds.Right - 1) { throw 'Text overlaps the application icon' }
        if ($textBounds.Right -gt $bounds.Right + 1 -or $textBounds.Bottom -gt $bounds.Bottom + 1) { throw 'Notification content extends beyond its window' }
    }
    return $bounds
}
function Assert-TransparentMargins {
    if (([IslandClick]::GetWindowLongPtr($islandHandle, -20).ToInt64() -band 8) -eq 0) { throw 'Notification window is not topmost before the backdrop check' }
    $reference = [IslandClick]::CreateUnderlay($islandHandle)
    if ($reference -eq [IntPtr]::Zero) { throw 'Could not create the backdrop reference' }
    try {
        Start-Sleep -Milliseconds 350
        $rect = New-Object IslandClick+Rect
        [void][IslandClick]::GetWindowRect($islandHandle, [ref]$rect)
        $point = New-Object IslandClick+Point
        $point.X = $rect.Left + 3; $point.Y = $rect.Top + 3
        $pixel = [IslandClick]::Pixel($point.X, $point.Y)
        $capture = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
        $graphics = [System.Drawing.Graphics]::FromImage($capture)
        try {
            $dc = $graphics.GetHdc()
            try { [IslandClick]::CaptureFrame($dc, $rect.Left, $rect.Top, $capture.Width, $capture.Height) }
            finally { $graphics.ReleaseHdc($dc) }
            $capture.Save((Join-Path (Split-Path $package.InstallLocation -Parent) 'verification/notification-host.png'))
        }
        finally { $graphics.Dispose(); $capture.Dispose() }
        if ($pixel -ne 0xFFFFFF) { throw "Transparent margins cover the backdrop: pixel=$('{0:X8}' -f $pixel), referenceVisible=$([IslandClick]::IsWindowVisible($reference)), referenceStyle=$('{0:X}' -f [IslandClick]::GetWindowLongPtr($reference, -20).ToInt64()), point=$($point.X),$($point.Y)" }
        if ([IslandClick]::HitTest($islandHandle, $point) -ne -1) { throw 'Transparent margins do not return pass-through hit testing' }
        [uint32]$owner = 0
        [void][IslandClick]::GetWindowThreadProcessId([IslandClick]::WindowFromPoint($point), [ref]$owner)
        if ($owner -eq $app.Id) { throw 'Transparent margins still intercept the underlying window' }
        if (([IslandClick]::GetWindowLongPtr($islandHandle, -20).ToInt64() -band 8) -eq 0) { throw 'Notification window lost its topmost style' }
    }
    finally { [void][IslandClick]::DestroyWindow($reference) }
}
function Open-TrayMenu {
    [void][IslandClick]::PostMessage($islandHandle, 0x8001, [UIntPtr]::Zero, [IntPtr]0x1007B)
    Start-Sleep -Milliseconds 500
    Assert-Idle
    $popup = [IslandClick]::FindPopup($app.Id)
    $status = [IslandClick]::MenuText($popup, 0)
    if ($popup -eq [IntPtr]::Zero -or $status -ne '通知监听中' -or [IslandClick]::MenuText($popup, 1) -ne '设置' -or [IslandClick]::MenuText($popup, 3) -ne '退出') {
        throw "Tray menu does not show the listening status and exit command; allow notification access first. Status: $status"
    }
    return $popup
}
function Close-TrayMenu {
    $menuOwner = [IslandClick]::FindWindow($app.Id, 'WindowsIsland.TrayMenu')
    [void][IslandClick]::PostMessage($menuOwner, 0x1F, [UIntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}

try {
    Assert-Idle
    'PASS: startup is tray-only with no visible island'
    $style = [IslandClick]::GetWindowLongPtr($islandHandle, -20).ToInt64()
    if (($style -band 0x08000080) -ne 0x08000080 -or ($style -band 0x40000) -ne 0) { throw 'Island can activate or appear in taskbar switchers' }
    $duplicate = Start-Process -FilePath $app.Path -ArgumentList '--verify-local' -WindowStyle Hidden -PassThru
    if (!$duplicate.WaitForExit(5000)) { throw 'Repeated launch started a second listener' }
    $instances = @(Get-Process WindowsIsland | Where-Object { $_.Path -eq $app.Path })
    if ($instances.Count -ne 1) { throw 'Expected exactly one tray listener' }
    Assert-Idle
    'PASS: repeated launch keeps one listener and leaves the island hidden'
    1..2 | ForEach-Object {
        [void](Open-TrayMenu)
        Close-TrayMenu
    }
    'PASS: tray menu can reopen without revealing the island'
    Send-Test 'first'
    Wait-Title 'first'
    Assert-NoControls
    Save-Island 'notification.png'
    [void](Assert-Layout 'Island test first' 'Local notification verification')
    Assert-SurfaceInput
    Assert-TransparentMargins
    'PASS: transparent margins preserve the backdrop and pass clicks through while the notification stays topmost'
    if (!(Find-Name 'Island test first')) { throw 'Pointer validation interrupted the notification' }
    'PASS: real Windows notification expands the island'
    Start-Sleep -Seconds 6
    Assert-Idle
    if (Find-Name 'Island test first') { throw 'Notification text was not cleared' }
    'PASS: notification disappears and island collapses automatically'

    Send-Test 'short' '收到' '好的，稍后见。'
    Wait-Title 'short' '收到'
    Save-Island 'notification-short.png'
    $shortBounds = Assert-Layout '收到' '好的，稍后见。'
    $longTitle = '这是一条需要换行显示的较长通知标题，用来验证内容优先的布局'
    $longBody = ('详细消息正文包含中文、English 和 emoji 🙂，窗口应根据内容展开，并在达到上限后省略。' * 8)
    Send-Test 'long' $longTitle $longBody
    Wait-Title 'long' $longTitle
    Save-Island 'notification-long.png'
    $longBounds = Assert-Layout $longTitle $longBody
    if ($longBounds.Width -le $shortBounds.Width -or $longBounds.Height -le $shortBounds.Height) { throw "Long message did not grow from short message size: short=$shortBounds, long=$longBounds" }
    Send-Test 'title-only' '完成' ''
    Wait-Title 'title-only' '完成'
    Save-Island 'notification-title-only.png'
    $smallBounds = Assert-Layout '完成'
    if ($smallBounds.Width -ge $longBounds.Width -or $smallBounds.Height -ge $shortBounds.Height) { throw "Title-only message did not shrink: short=$shortBounds, title-only=$smallBounds" }
    'PASS: source app icon is visible and window grows and shrinks with content within its fixed maximum'
    Start-Sleep -Seconds 6
    Assert-Idle

    Send-Test 'closed'; Wait-Title 'closed'
    if ($SkipPointer) {
        [void][IslandClick]::PostMessage($islandHandle, 0x10, [UIntPtr]::Zero, [IntPtr]::Zero)
    } else { Invoke-IslandPointer $true }
    Start-Sleep -Milliseconds 500
    Assert-Idle
    if ($SkipPointer) { 'PASS: closing the window hides the notification; physical right-click verification was skipped.' }
    else { 'PASS: right-clicking the notification hides it while the tray listener keeps running' }

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
    if ($VerifyExit -and !$SkipPointer) {
        $popup = Open-TrayMenu
        $point = [IslandClick]::MenuPoint($popup, 3)
        $original = New-Object IslandClick+Point
        [void][IslandClick]::GetCursorPos([ref]$original)
        try {
            [void][IslandClick]::SetCursorPos($point.X, $point.Y)
            Start-Sleep -Milliseconds 100
            [IslandClick]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 75
            [IslandClick]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 500
        }
        finally { [void][IslandClick]::SetCursorPos($original.X, $original.Y) }
        if (!$app.WaitForExit(5000)) {
            throw "Tray exit did not stop the listener; icon=$([IslandClick]::HasTrayIcon($islandHandle)), popup=$([IslandClick]::FindPopup($app.Id))"
        }
        if ([IslandClick]::HasTrayIcon($islandHandle)) { throw 'Tray exit did not remove the tray icon' }
        'PASS: tray exit stops the listener and removes its icon'
    }
}
finally {
    Close-TrayMenu
    foreach ($tag in $tags) {
        [Windows.UI.Notifications.ToastNotificationManager]::History.Remove($tag, $group, $appId)
    }
}
