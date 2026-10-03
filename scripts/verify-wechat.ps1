[CmdletBinding()]
param([switch]$SkipPointer)

$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot 'verify-notifications.ps1') -Raw -Encoding UTF8
& ([scriptblock]::Create($source.Substring(0, $source.IndexOf('$package ='))))
$workspace = Split-Path $PSScriptRoot -Parent
$executable = Join-Path $workspace 'artifacts/publish/WindowsIsland.exe'
if (Get-Process WindowsIsland -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }) {
    throw 'Exit Windows Island before starting this isolated check.'
}
$package = Get-AppxPackage -Name WindowsIsland.Desktop
if (!$package) { throw 'Register Windows Island first.' }
$fixtureSource = Get-ChildItem (Join-Path $workspace 'artifacts/bin/WindowsIsland.WeChatTests') -Recurse -Filter WindowsIsland.WeChatTests.exe | Select-Object -First 1
if (!$fixtureSource) { throw 'Build WindowsIsland.WeChatTests first.' }
$fixtureExecutable = Join-Path $fixtureSource.DirectoryName 'WeChat.exe'
Copy-Item -LiteralPath $fixtureSource.FullName -Destination $fixtureExecutable -Force
$token = [Guid]::NewGuid().ToString('N')
$ready = Join-Path $fixtureSource.DirectoryName "$token.ready"
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $token, [IO.Pipes.PipeDirection]::Out)
$writer = $null
$fixture = $null

function Find-Id([string]$id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-Message([string]$title, [string]$body) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt 8) {
        if ([IslandClick]::IsWindowVisible($handle)) {
            $titleElement = Find-Id 'NotificationTitle'
            $bodyElement = Find-Id 'NotificationBody'
            if ($titleElement -and $bodyElement -and $titleElement.Current.Name -eq $title -and $bodyElement.Current.Name -eq $body) { return }
        }
        Start-Sleep -Milliseconds 30
    }
    throw 'Accessible WeChat content did not reach the notification window.'
}

function Open-NotificationSource {
    Start-Sleep -Milliseconds 350
    $surface = (Find-Id 'IslandSurface').Current.BoundingRectangle
    $original = New-Object IslandClick+Point
    [void][IslandClick]::GetCursorPos([ref]$original)
    try {
        $target = New-Object IslandClick+Point
        $target.X = [int]($surface.Left + $surface.Width / 2)
        $target.Y = [int]($surface.Top + $surface.Height / 2)
        [uint32]$targetOwner = 0
        [void][IslandClick]::GetWindowThreadProcessId([IslandClick]::WindowFromPoint($target), [ref]$targetOwner)
        if ($targetOwner -ne $app.Id) { throw 'The notification did not receive input at its center.' }
        if (![IslandClick]::SetCursorPos($target.X, $target.Y)) { throw 'Could not move the pointer to the notification.' }
        Start-Sleep -Milliseconds 75
        $position = New-Object IslandClick+Point
        [void][IslandClick]::GetCursorPos([ref]$position)
        if ($position.X -ne $target.X -or $position.Y -ne $target.Y) {
            $clip = New-Object IslandClick+Rect
            [void][IslandClick]::GetClipCursor([ref]$clip)
            throw "The pointer could not reach the notification: target=$($target.X),$($target.Y) actual=$($position.X),$($position.Y) clip=$($clip.Left),$($clip.Top),$($clip.Right),$($clip.Bottom)"
        }
        [IslandClick]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 75
        [IslandClick]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    } finally { [void][IslandClick]::SetCursorPos($original.X, $original.Y) }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt 3) {
        [uint32]$owner = 0
        [void][IslandClick]::GetWindowThreadProcessId([IslandClick]::GetForegroundWindow(), [ref]$owner)
        if ($owner -eq $fixture.Id -and ![IslandClick]::IsWindowVisible($handle)) { return }
        Start-Sleep -Milliseconds 30
    }
    throw 'Left-clicking did not restore the source application and dismiss the notification.'
}

try {
    $fixture = Start-Process -FilePath $fixtureExecutable -ArgumentList @('--fixture', $token, ('"' + $ready + '"')) -WindowStyle Hidden -PassThru
    $pipe.Connect(8000)
    $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    [void][IslandClick]::LaunchRegistered("$($package.PackageFamilyName)!App", '--verify-local')
    Start-Sleep -Seconds 3
    $app = Get-Process WindowsIsland | Where-Object { $_.Path -eq $executable } | Select-Object -First 1
    if (!$app) { throw 'Could not launch the isolated listener.' }
    $handle = [IslandClick]::FindIsland($app.Id)
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    if ([IslandClick]::IsWindowVisible($handle)) { throw 'Connecting replayed message history.' }
    'PASS: connecting to the WeChat fixture leaves existing history silent.'

    $foreground = [IslandClick]::GetForegroundWindow()
    $writer.WriteLine('incoming:第一条实时消息')
    Wait-Message '示例会话' '第一条实时消息'
    Start-Sleep -Milliseconds 350
    foreach ($id in @('NotificationAppName', 'NotificationAppIcon', 'NotificationHeaderIcon', 'NotificationTime')) {
        $element = Find-Id $id
        if (!$element) { throw "WeChat notification element is missing: $id" }
        if ($element.Current.IsOffscreen) {
            $rect = New-Object IslandClick+Rect
            [void][IslandClick]::GetWindowRect($handle, [ref]$rect)
            throw "WeChat element is offscreen: $id element=$($element.Current.BoundingRectangle) surface=$((Find-Id 'IslandSurface').Current.BoundingRectangle) window=$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom)"
        }
    }
    if ((Find-Id 'NotificationAppName').Current.Name -ne '微信') { throw 'WeChat application identity was lost.' }
    if (([IslandClick]::GetWindowLongPtr($handle, -20).ToInt64() -band 8) -eq 0) { throw 'WeChat notification is not topmost.' }
    if ([IslandClick]::GetForegroundWindow() -ne $foreground) { throw 'WeChat notification changed foreground focus.' }
    $icon = (Find-Id 'NotificationAppIcon').Current.BoundingRectangle
    $title = (Find-Id 'NotificationTitle').Current.BoundingRectangle
    $body = (Find-Id 'NotificationBody').Current.BoundingRectangle
    $header = (Find-Id 'NotificationHeaderIcon').Current.BoundingRectangle
    if ($title.Left -lt $icon.Right - 1 -or $body.Left -lt $icon.Right - 1 -or $header.Bottom -gt $icon.Top + 1) {
        throw 'WeChat icon and content overlap.'
    }
    'PASS: UIAutomation content reaches the topmost island with both application icons, time and unchanged focus.'

    $surface = (Find-Id 'IslandSurface').Current.BoundingRectangle
    $output = Join-Path $workspace 'artifacts/verification'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $capture = [Drawing.Bitmap]::new([int]$surface.Width, [int]$surface.Height)
    $graphics = [Drawing.Graphics]::FromImage($capture)
    try {
        $dc = $graphics.GetHdc()
        try { [IslandClick]::CaptureFrame($dc, [int]$surface.Left, [int]$surface.Top, $capture.Width, $capture.Height) }
        finally { $graphics.ReleaseHdc($dc) }
        $capture.Save((Join-Path $output 'wechat-fixture.png'))
    } finally { $graphics.Dispose(); $capture.Dispose() }

    $writer.WriteLine('minimize')
    Start-Sleep -Milliseconds 250
    $writer.WriteLine('incoming:最小化后收到的消息')
    Wait-Message '示例会话' '最小化后收到的消息'
    'PASS: messages from a minimized WeChat fixture continue reaching the island.'
    if (!$SkipPointer) {
        Open-NotificationSource
        'PASS: left-clicking restores the minimized source application and dismisses its notification.'
    } else { 'SKIP: physical pointer verification was explicitly disabled.' }
    $writer.WriteLine('group:小林: 群聊的新消息')
    Wait-Message '示例群' '小林: 群聊的新消息'
    'PASS: an unread conversation preview reaches the island without opening that conversation.'
    Start-Sleep -Seconds 6
    if ([IslandClick]::IsWindowVisible($handle)) { throw 'WeChat notification did not disappear automatically.' }
    $writer.WriteLine('mute:免打扰会话的新消息')
    Start-Sleep -Seconds 2
    if ([IslandClick]::IsWindowVisible($handle)) { throw 'A muted WeChat conversation produced a notification.' }
    $writer.WriteLine('unmute:免打扰会话的新消息')
    Start-Sleep -Seconds 1
    if ([IslandClick]::IsWindowVisible($handle)) { throw 'Disabling mute replayed old messages.' }
    $writer.WriteLine('group:小林: 取消免打扰后的新消息')
    Wait-Message '示例群' '小林: 取消免打扰后的新消息'
    'PASS: muted conversations remain silent, unmuting preserves history, and subsequent new messages arrive.'
    Start-Sleep -Seconds 6
    $writer.WriteLine('outgoing:这是发出的消息')
    Start-Sleep -Seconds 1
    if ([IslandClick]::IsWindowVisible($handle)) { throw 'An outgoing message produced a notification.' }
    'PASS: WeChat notifications disappear automatically and outgoing messages remain silent.'
} finally {
    if ($fixture -and !$fixture.HasExited) {
        if ($writer) { try { $writer.WriteLine('quit') } catch [IO.IOException] {} }
        if (!$fixture.WaitForExit(3000)) {
            if ($fixture.Path -ne $fixtureExecutable) { throw 'Unexpected fixture path.' }
            Stop-Process -Id $fixture.Id
        }
    }
    if ($writer) { $writer.Dispose() }
    $pipe.Dispose()
    if (Test-Path -LiteralPath $ready) { Remove-Item -LiteralPath $ready }
}
