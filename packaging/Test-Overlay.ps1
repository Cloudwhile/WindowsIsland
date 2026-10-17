#Requires -Version 7.0
param([string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms,System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class IslandOverlayProbe
{
    public delegate bool Visitor(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(Visitor visitor, IntPtr data);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static IntPtr Find(int process, string title)
    {
        var found = IntPtr.Zero;
        EnumWindows((window, unused) => {
            int owner; GetWindowThreadProcessId(window, out owner);
            if (owner != process || !IsWindowVisible(window)) return true;
            var name = new StringBuilder(256); GetWindowText(window, name, name.Capacity);
            if (name.ToString() == title) { found = window; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
$previousDpi = [IslandOverlayProbe]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$work = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('../artifacts/verification/overlay/' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$payload = Join-Path $work 'app'
New-Item -ItemType Directory -Path $payload | Out-Null
foreach ($item in Get-ChildItem -LiteralPath (Resolve-Path $PublishDirectory).Path -Force) {
    if ($item.Name -notlike 'verification-*') { Copy-Item -LiteralPath $item.FullName -Destination $payload -Recurse }
}
$executable = Join-Path $payload 'WindowsIsland.exe'
$backdrop = [Windows.Forms.Form]::new()
$backdrop.FormBorderStyle = 'None'
$backdrop.StartPosition = 'Manual'
$backdrop.Bounds = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$backdrop.BackColor = [Drawing.Color]::FromArgb(28,116,132)
$backdrop.Text = 'Island overlay verification'
$backdrop.TopMost = $true
$backdrop.Show()
[Windows.Forms.Application]::DoEvents()
function Wait-For([scriptblock]$condition) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 80
    } while ($watch.Elapsed.TotalSeconds -lt 20)
    throw 'Overlay verification timed out.'
}
function Find-Control($root, [string]$id) {
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
}
$process = $null
try {
    foreach ($theme in @('light','dark')) {
        foreach ($position in 0..7) {
            $settings = @{ SetupCompleted=$true; SystemNotifications=$false; WeChat=$false; Telegram=$false; Power=$false;
                Animations=$true; Acrylic=$true; OpacityTransitions=$true; BackgroundOpacity=0.9; Position=$position;
                MuteDuringGames=$false; MuteDuringFullScreen=$false; LocalDiagnostics=$false; Language='zh-CN' }
            $settings | ConvertTo-Json | Set-Content (Join-Path $payload 'verification-settings.json') -Encoding UTF8
            $process = Start-Process -FilePath $executable -ArgumentList '--verify-local',"--verify-$theme",'--settings' -WindowStyle Hidden -PassThru
            $root = Wait-For {
                $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
                    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id))
                foreach ($window in $windows) { if (Find-Control $window 'PreviewNotification') { return $window } }
            }
            $preview = Find-Control $root 'PreviewNotification'
            $backdrop.Activate()
            [void][IslandOverlayProbe]::SetForegroundWindow($backdrop.Handle)
            [Windows.Forms.Application]::DoEvents()
            $foreground = [IslandOverlayProbe]::GetForegroundWindow()
            $preview.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            $window = Wait-For {
                $handle = [IslandOverlayProbe]::Find($process.Id, 'Windows Island')
                if ($handle -ne [IntPtr]::Zero) { $handle }
            }
            Start-Sleep -Milliseconds 750
            if ([IslandOverlayProbe]::GetForegroundWindow() -ne $foreground) { throw 'Notification stole input focus.' }
            $rect = [IslandOverlayProbe+Rect]::new()
            [void][IslandOverlayProbe]::GetWindowRect($window, [ref]$rect)
            $bitmap = [Drawing.Bitmap]::new(($rect.Right-$rect.Left), ($rect.Bottom-$rect.Top))
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size)
                $bitmap.Save((Join-Path $work "$theme-$position.png"))
                # The host is wider than the pill, leaving margins well outside
                # its shadow. Those pixels must show the actual window behind it.
                foreach ($x in @(3, ($bitmap.Width-4))) {
                    foreach ($y in @(3, [int]($bitmap.Height/2), ($bitmap.Height-4))) {
                        $pixel = $bitmap.GetPixel($x,$y)
                        $distance = [Math]::Abs($pixel.R-28)+[Math]::Abs($pixel.G-116)+[Math]::Abs($pixel.B-132)
                        if ($distance -gt 30) { throw "Opaque host background: $theme position $position at $x,$y ($pixel)" }
                    }
                }
            } finally { $graphics.Dispose(); $bitmap.Dispose() }
            $inputWindow = [IslandOverlayProbe]::Find($process.Id, 'WindowsIsland.NotificationInput')
            if ($inputWindow -eq [IntPtr]::Zero) { throw 'Notification input window missing.' }
            [void][IslandOverlayProbe]::SendMessage($inputWindow,0x205,[IntPtr]::Zero,[IntPtr]::Zero)
            $null = Wait-For { ![IslandOverlayProbe]::IsWindowVisible($window) }
            # A second preview after dismissal must still reach the overlay.
            $preview.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            $null = Wait-For { [IslandOverlayProbe]::IsWindowVisible($window) }
            if ($process.Path -ne $executable) { throw 'Unexpected test process path.' }
            Stop-Process -Id $process.Id
            [void]$process.WaitForExit(5000)
            $process.Dispose(); $process=$null
            "PASS $theme position ${position}: transparent margins, focus preserved, dismiss and redisplay"
        }
    }
} finally {
    if ($process -and !$process.HasExited -and $process.Path -eq $executable) { Stop-Process -Id $process.Id }
    if ($process) { $process.Dispose() }
    $backdrop.Close(); $backdrop.Dispose()
    [void][IslandOverlayProbe]::SetThreadDpiAwarenessContext($previousDpi)
    "Verification output: $work"
}
