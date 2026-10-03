[CmdletBinding()]
param([switch]$Fixture)

$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot 'verify-notifications.ps1') -Raw -Encoding UTF8
& ([scriptblock]::Create($source.Substring(0, $source.IndexOf('$package ='))))
$workspace = Split-Path $PSScriptRoot -Parent
$executable = [System.IO.Path]::GetFullPath((Join-Path $workspace 'artifacts/publish/WindowsIsland.exe'))
$app = Get-Process WindowsIsland -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable } | Select-Object -First 1
$ownedFixture = $null
$fixtureExecutable = [System.IO.Path]::GetFullPath((Join-Path $workspace 'artifacts/native/Release/WindowsIsland.TelegramFixture.exe'))
if ($Fixture) {
    if ($app) { Stop-Process -Id $app.Id; [void]$app.WaitForExit(5000); Start-Sleep -Seconds 1 }
    $package = Get-AppxPackage -Name WindowsIsland.Desktop
    [void][IslandClick]::LaunchRegistered("$($package.PackageFamilyName)!App", '--verify-local')
    Start-Sleep -Seconds 2
    $app = Get-Process WindowsIsland | Where-Object { $_.Path -eq $executable } | Select-Object -First 1
    $ownedFixture = Start-Process -FilePath $fixtureExecutable -WindowStyle Hidden -PassThru
    $client = $ownedFixture
} else {
    $client = Get-Process Telegram | Select-Object -First 1
}
if (!$app) { throw 'Start the tray listener first.' }
try {
$handle = [IslandClick]::FindIsland($app.Id)
$root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)

function Find-Id([string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

$timeout = [Diagnostics.Stopwatch]::StartNew()
while ($timeout.Elapsed.TotalSeconds -lt 60) {
    if ([IslandClick]::IsWindowVisible($handle)) {
        $name = Find-Id 'NotificationAppName'
        $avatar = Find-Id 'NotificationAvatar'
        if ($name -and $name.Current.Name -eq 'Telegram' -and $avatar -and !$avatar.Current.IsOffscreen) { break }
    }
    Start-Sleep -Milliseconds 100
}
if ($timeout.Elapsed.TotalSeconds -ge 60) { throw 'No actual Telegram popup reached the island within the observation window.' }
$badge = Find-Id 'NotificationHeaderIcon'
$title = Find-Id 'NotificationTitle'
$body = Find-Id 'NotificationBody'
$time = Find-Id 'NotificationTime'
foreach ($element in @($avatar, $badge, $title, $body, $time)) {
    if (!$element -or $element.Current.IsOffscreen) { throw 'Telegram content, avatar, badge or timestamp is missing.' }
}
$avatarBounds = $avatar.Current.BoundingRectangle
$titleBounds = $title.Current.BoundingRectangle
$bodyBounds = $body.Current.BoundingRectangle
$timeBounds = $time.Current.BoundingRectangle
$surface = Find-Id 'IslandSurface'
if (!$surface) { throw 'Notification surface is missing.' }
$windowBounds = $surface.Current.BoundingRectangle
if ($titleBounds.Left -lt $avatarBounds.Right - 1 -or $bodyBounds.Left -lt $avatarBounds.Right - 1 -or
    $titleBounds.Right -gt $windowBounds.Right + 1 -or $bodyBounds.Bottom -gt $windowBounds.Bottom -or
    $badge.Current.BoundingRectangle.Bottom -gt $avatarBounds.Top + 1 -or $timeBounds.Bottom -gt $titleBounds.Top + 1) {
    throw 'Telegram text overlaps the avatar or timestamp, or extends beyond the island.'
}
if ([string]::IsNullOrWhiteSpace($title.Current.Name) -or [string]::IsNullOrWhiteSpace($body.Current.Name)) {
    throw 'Telegram original title or body is blank.'
}
$client.Refresh()
if (!($client.Modules | Where-Object { $_.ModuleName -eq 'WindowsIsland.TelegramHook.dll' })) { throw 'Native bridge is not attached.' }
"PASS: Telegram notification reaches the normal tray app with original title ($($title.Current.Name.Length) chars), body ($($body.Current.Name.Length) chars), avatar, app badge and timestamp."
'PASS: Header, body and timestamp fit the adaptive capsule without text overlaps.'

# Visual QA stores only the avatar and badge, never message content.
$capture = New-Object System.Drawing.Bitmap([int]($avatarBounds.Width + 6), [int]($avatarBounds.Height + 6))
$graphics = [System.Drawing.Graphics]::FromImage($capture)
try {
    $dc = $graphics.GetHdc()
    try { [IslandClick]::CaptureFrame($dc, [int]($avatarBounds.Left - 3), [int]($avatarBounds.Top - 3), $capture.Width, $capture.Height) }
    finally { $graphics.ReleaseHdc($dc) }
    $capture.Save((Join-Path $workspace 'artifacts/verification/telegram-avatar.png'))
} finally { $graphics.Dispose(); $capture.Dispose() }

if ($Fixture) {
    $capture = New-Object System.Drawing.Bitmap([int]$windowBounds.Width, [int]$windowBounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($capture)
    try {
        $dc = $graphics.GetHdc()
        try { [IslandClick]::CaptureFrame($dc, [int]$windowBounds.Left, [int]$windowBounds.Top, $capture.Width, $capture.Height) }
        finally { $graphics.ReleaseHdc($dc) }
        $capture.Save((Join-Path $workspace 'artifacts/verification/telegram-fixture.png'))
    } finally { $graphics.Dispose(); $capture.Dispose() }
}
} finally {
    if ($Fixture) {
        if ($ownedFixture -and !$ownedFixture.HasExited) {
            if ($ownedFixture.Path -ne $fixtureExecutable) { throw 'Unexpected fixture path.' }
            Stop-Process -Id $ownedFixture.Id
            [void]$ownedFixture.WaitForExit(5000)
        }
        if ($app -and !$app.HasExited) {
            if ($app.Path -ne $executable) { throw 'Unexpected listener path.' }
            Stop-Process -Id $app.Id
            [void]$app.WaitForExit(5000)
        }
        Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$($package.PackageFamilyName)!App" -WindowStyle Hidden
        Start-Sleep -Seconds 2
        'Restored the normal registered tray listener.'
    }
}
