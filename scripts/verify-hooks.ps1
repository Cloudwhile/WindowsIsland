[CmdletBinding()]
param([switch]$Isolated)

$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot 'verify-notifications.ps1') -Raw -Encoding UTF8
& ([scriptblock]::Create($source.Substring(0, $source.IndexOf('$package ='))))
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
public sealed class IslandAnimationProbe : IDisposable {
    private readonly IntPtr window;
    private readonly Thread thread;
    private readonly List<double> frames = new List<double>();
    private volatile bool stopped;
    public IslandAnimationProbe(IntPtr handle) {
        window = handle;
        thread = new Thread(Sample) { IsBackground = true };
        thread.Start();
    }
    private void Sample() {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        var clock = Stopwatch.StartNew();
        Rect previous = new Rect();
        while (!stopped) {
            Rect bounds;
            if (IsWindowVisible(window) && GetWindowRect(window, out bounds)
                && (bounds.Left != previous.Left || bounds.Top != previous.Top
                    || bounds.Right != previous.Right || bounds.Bottom != previous.Bottom)) {
                lock (frames) frames.Add(clock.Elapsed.TotalMilliseconds);
                previous = bounds;
            }
            Thread.Sleep(3);
        }
    }
    public double[] Snapshot() { lock (frames) return frames.ToArray(); }
    public void Dispose() { stopped = true; thread.Join(1000); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
$workspace = Split-Path $PSScriptRoot -Parent
$package = Get-AppxPackage -Name WindowsIsland.Desktop
if (!$package) { throw 'Register Windows Island first.' }
$executable = Join-Path $workspace 'artifacts/publish/WindowsIsland.exe'
$app = Get-Process WindowsIsland -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable } | Select-Object -First 1
if (!$app) {
    if ($Isolated) {
        [void][IslandClick]::LaunchRegistered("$($package.PackageFamilyName)!App", '--verify-local')
    } else {
        Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$($package.PackageFamilyName)!App" -WindowStyle Hidden
    }
    Start-Sleep -Seconds 2
    $app = Get-Process WindowsIsland -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable } | Select-Object -First 1
}
if (!$app) { throw 'Could not start the registered tray app.' }
$handle = [IslandClick]::FindIsland($app.Id)
$root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$fixture = Join-Path $workspace 'artifacts/bin/WindowsIsland.HookFixture/Release/net10.0-windows/win-x64'
if (!(Test-Path -LiteralPath (Join-Path $fixture 'WindowsIsland.HookFixture.exe'))) { throw 'Build the hook fixture first.' }
$testOutput = Join-Path $workspace 'artifacts/verification/hooks'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null

function Find-Text([string]$text) {
    if (![IslandClick]::IsWindowVisible($handle)) { return $null }
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $text)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

foreach ($client in @('WeChat', 'Telegram')) {
    $clientExecutable = Join-Path $fixture "$client.exe"
    Copy-Item -LiteralPath (Join-Path $fixture 'WindowsIsland.HookFixture.exe') -Destination $clientExecutable -Force
    $ready = Join-Path $testOutput "$client.ready"
    $title = "Hook fixture $client"
    $body = "Local popup verification $client"
    $probe = New-Object IslandAnimationProbe($handle)
    $arguments = @(('"' + $title + '"'), ('"' + $body + '"'), ('"' + $ready + '"'))
    if ($client -eq 'Telegram') { $arguments += '--update' }
    $testProcess = Start-Process -FilePath $clientExecutable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        $timeout = [Diagnostics.Stopwatch]::StartNew()
        while ($timeout.Elapsed.TotalSeconds -lt 13) {
            if (Find-Text $body) { break }
            Start-Sleep -Milliseconds 3
        }
        if (!(Find-Text $title) -or !(Find-Text $body)) { throw "Client hook did not receive the local popup: $client" }
        $display = if ($client -eq 'WeChat') { '微信' } else { $client }
        if (!(Find-Text $display)) { throw "Client popup had the wrong application name: $client" }
        $frames = $probe.Snapshot()
        if ($frames.Count -lt 4) { throw "Window animation did not produce enough intermediate sizes: $client ($($frames.Count))" }
        $activeMilliseconds = $frames[$frames.Count - 1] - $frames[0]
        Write-Output "PASS: $client popup hook displays real accessible title and body ($($frames.Count) sampled size changes over $([int]$activeMilliseconds) ms)."
        if ($client -eq 'Telegram') { Write-Output 'PASS: rapid updates of the same popup preserve its final message text.' }
        Start-Sleep -Seconds 6
        if ([IslandClick]::IsWindowVisible($handle)) { throw 'Hook notification did not return to the tray.' }
        if (![IslandClick]::HasTrayIcon($handle)) { throw 'Tray icon was lost after a hook notification.' }
        if (!$testProcess.WaitForExit(6000)) { throw "Fixture did not finish: $client" }
    }
    finally {
        $probe.Dispose()
        if (!$testProcess.HasExited) {
            if ($testProcess.Path -ne $clientExecutable) { throw 'Unexpected fixture executable path.' }
            Stop-Process -Id $testProcess.Id
        }
    }
}

# A real OS status query on power broadcasts must leave the current baseline silent.
[void][IslandClick]::PostMessage($handle, 0x0218, [UIntPtr]::new([uint32]0x000A), [IntPtr]::Zero)
Start-Sleep -Milliseconds 400
if ([IslandClick]::IsWindowVisible($handle)) { throw 'Unchanged power status produced a popup.' }
Write-Output 'PASS: power broadcast keeps the seeded baseline silent and the tray listener alive.'
