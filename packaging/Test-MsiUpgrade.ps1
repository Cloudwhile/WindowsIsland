param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'),
    [string]$Wix = (Join-Path $PSScriptRoot '../artifacts/tools/wix/wix.exe'),
    [ValidateSet('enabled', 'blocked', 'missing', 'foreign', 'rollback', 'running-install', 'unresponsive')]
    [string[]]$Cases = @('enabled', 'blocked', 'missing', 'foreign', 'rollback', 'running-install', 'unresponsive')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$wixPath = (Resolve-Path -LiteralPath $Wix).Path
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id = [Guid]::NewGuid().ToString('N')
$work = Join-Path $repository "artifacts/verification/msi-upgrade/$id"
$scope = "Software\Cloudwhile\WindowsIsland\UpgradeVerification\$id"
$runKey = "$scope\Run"
$approvalKey = "$scope\Approval"
$install = Join-Path $work 'installed with spaces'
$normalInstall = (Get-ItemPropertyValue 'HKCU:\Software\Cloudwhile\WindowsIsland\Installer' -Name InstallDirectory -ErrorAction SilentlyContinue)
$normalProcesses = @(Get-Process WindowsIsland -ErrorAction SilentlyContinue | ForEach-Object { @{ Id = $_.Id; Path = $_.Path } })
New-Item -ItemType Directory -Path "$work/native","$work/packaging","$work/payload","$work/distribution" -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination "$work/LICENSE"

# Every identity is isolated, including file component GUID seeds and startup registry paths.
$build = Get-Content (Join-Path $PSScriptRoot 'Build-Msi.ps1') -Raw -Encoding UTF8
$build = $build.Replace('WindowsIsland/x64/perUser/', "WindowsIsland/UpgradeVerification/$id/")
$build = $build.Replace('Software\Cloudwhile\WindowsIsland\Installer', "$scope\Installer")
[IO.File]::WriteAllText("$work/packaging/Build-Msi.ps1", $build)
$uninstall = Get-Content (Join-Path $PSScriptRoot 'Uninstall.ps1') -Raw -Encoding UTF8
$uninstall = $uninstall.Replace('Software\Microsoft\Windows\CurrentVersion\Run', $runKey)
$uninstall = $uninstall.Replace('Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', $approvalKey)
$uninstall = $uninstall.Replace('WindowsIsland.Desktop', "WindowsIsland.UpgradeVerification$id")
[IO.File]::WriteAllText("$work/packaging/Uninstall.ps1", $uninstall, [Text.UTF8Encoding]::new($true))
$source = Get-Content (Join-Path $repository 'native/installer/startup.cpp') -Raw -Encoding UTF8
$source = $source.Replace('Software\\Microsoft\\Windows\\CurrentVersion\\Run', $runKey.Replace('\', '\\'))
$source = $source.Replace('Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run', $approvalKey.Replace('\', '\\'))
if ($source.Contains('Software\\Microsoft\\Windows') -or $uninstall.Contains('Software\Microsoft\Windows')) {
    throw 'Startup registry paths were not fully isolated.'
}
$source += '
extern "C" __declspec(dllexport) UINT __stdcall FailUpgrade(MSIHANDLE) { return ERROR_INSTALL_FAILURE; }
'
[IO.File]::WriteAllText("$work/native/startup.cpp", $source)
[IO.File]::WriteAllText("$work/native/probe.cpp", 'extern "C" __declspec(dllexport) int PayloadRevision() { return PAYLOAD_REVISION; }')
[IO.File]::WriteAllText("$work/native/running.cpp", @'
#include <windows.h>
#include <sddl.h>
UINT exitMessage = 0;
bool FixtureFlag(const wchar_t* name) {
    wchar_t value[2]{};
    return GetEnvironmentVariableW(name, value, 2) == 1 && value[0] == L'1';
}
LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == exitMessage) {
        if (!FixtureFlag(L"WINDOWS_ISLAND_FIXTURE_IGNORE_EXIT")) DestroyWindow(window);
        return 0;
    }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wparam, lparam);
}
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    auto payload = LoadLibraryW(L"versioned-payload.dll");
    if (!payload) return 2;
    exitMessage = RegisterWindowMessageW(L"WindowsIsland.ExitForMaintenance");
    WNDCLASSW type{};
    type.lpfnWndProc = WindowProc;
    type.hInstance = instance;
    type.lpszClassName = L"WindowsIsland.MsiRunningFixture";
    if (!RegisterClassW(&type)) return 3;
    auto window = CreateWindowExW(0, type.lpszClassName, L"", 0, 0, 0, 0, 0, nullptr, nullptr, instance, nullptr);
    if (!window) return 4;
    if (FixtureFlag(L"WINDOWS_ISLAND_FIXTURE_DENY_TERMINATE")) {
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:(D;;0x0001;;;WD)(A;;0x1fffff;;;OW)(A;;0x1fffff;;;SY)", SDDL_REVISION_1, &descriptor, nullptr)) return 5;
        auto secured = SetKernelObjectSecurity(GetCurrentProcess(), DACL_SECURITY_INFORMATION, descriptor);
        LocalFree(descriptor);
        if (!secured) return 6;
    }
    wchar_t readyName[256]{};
    if (!GetEnvironmentVariableW(L"WINDOWS_ISLAND_FIXTURE_READY", readyName, 256)) return 7;
    auto ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, readyName);
    if (!ready || !SetEvent(ready)) return 8;
    CloseHandle(ready);
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) DispatchMessageW(&message);
    FreeLibrary(payload);
    return 0;
}
'@)
[IO.File]::WriteAllText("$work/native/probe.rc", @'
#include <windows.h>
1 VERSIONINFO
 FILEVERSION 0,1,0,0
 PRODUCTVERSION 0,1,0,0
 FILEOS VOS_NT_WINDOWS32
 FILETYPE VFT_DLL
BEGIN
 BLOCK "StringFileInfo"
 BEGIN
  BLOCK "040904b0"
  BEGIN
   VALUE "FileVersion", "0.1.0.0"
   VALUE "ProductVersion", PAYLOAD_LABEL
  END
 END
 BLOCK "VarFileInfo"
 BEGIN
  VALUE "Translation", 0x0409, 1200
 END
END
'@)
[IO.File]::WriteAllText("$work/native/CMakeLists.txt", @'
cmake_minimum_required(VERSION 3.21)
project(WindowsIslandUpgradeVerification LANGUAGES CXX RC)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_library(UpgradeActions SHARED startup.cpp)
target_compile_definitions(UpgradeActions PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(UpgradeActions PRIVATE /W4 /utf-8 /EHsc)
target_link_libraries(UpgradeActions PRIVATE msi advapi32)
add_library(ProbeBefore SHARED probe.cpp probe.rc)
add_library(ProbeAfter SHARED probe.cpp probe.rc)
add_executable(RunningApplication WIN32 running.cpp)
target_link_libraries(RunningApplication PRIVATE advapi32)
set_target_properties(RunningApplication PROPERTIES OUTPUT_NAME WindowsIsland)
target_compile_definitions(ProbeBefore PRIVATE PAYLOAD_REVISION=4 PAYLOAD_LABEL="0.1.0-rc.4")
target_compile_definitions(ProbeAfter PRIVATE PAYLOAD_REVISION=5 PAYLOAD_LABEL="0.1.0-rc.5")
'@)
& cmake -S "$work/native" -B "$work/native/build" -A x64
if ($LASTEXITCODE) { throw 'Upgrade verification configuration failed.' }
& cmake --build "$work/native/build" --config Release
if ($LASTEXITCODE) { throw 'Upgrade verification native build failed.' }

$definition = Get-Content (Join-Path $PSScriptRoot 'WindowsIsland.wxs') -Raw -Encoding UTF8
$definition = $definition.Replace('8B397E71-1AA3-4F3B-911B-C08E0FAB484E', [Guid]::NewGuid().ToString())
$definition = $definition.Replace('C7CB66D6-D2C1-426E-BED2-3FAF199B6C95', [Guid]::NewGuid().ToString())
$definition = $definition.Replace('Software\Cloudwhile\WindowsIsland\Installer', "$scope\Installer")
$definition = $definition.Replace('CompressionLevel="high"', 'CompressionLevel="mszip"')
$definition = $definition.Replace('Windows Island', "Windows Island Upgrade Test $id")
$legacy = [xml]$definition.Replace('Scope="perUserOrMachine"', 'Scope="perUser"')
$ns = [Xml.XmlNamespaceManager]::new($legacy.NameTable)
$ns.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
foreach ($node in @($legacy.SelectNodes('//w:Binary|//w:CustomAction[@Id="SelectInstallScope" or @Id="CloseApplication" or @Id="CaptureStartup" or @Id="RestoreStartup" or @Id="RollbackStartup"]|//w:Property[@Id="RestoreStartup" or @Id="RollbackStartup" or @Id="REINSTALLMODE"]|//w:Custom[@Action="SelectInstallScope" or @Action="CloseApplication" or @Action="CaptureStartup" or @Action="RestoreStartup" or @Action="RollbackStartup"]|//w:Publish[@Value="SelectInstallScope"]', $ns))) {
    [void]$node.ParentNode.RemoveChild($node)
}
$legacy.SelectSingleNode('//w:MajorUpgrade', $ns).SetAttribute('Schedule', 'afterInstallValidate')
# Recreate the rc.4 install/uninstall behavior with isolated identities; do not install the public MSI.
Copy-Item -Path (Join-Path $publish '*') -Destination "$work/payload" -Recurse -Force
function Build-Package([string]$version, [string]$definitionText, [string]$marker) {
    [IO.File]::WriteAllText("$work/packaging/WindowsIsland.wxs", $definitionText)
    [IO.File]::WriteAllText("$work/payload/upgrade-marker.txt", $marker)
    $probe = if ($marker -eq 'legacy') { 'ProbeBefore' } else { 'ProbeAfter' }
    Copy-Item -LiteralPath "$work/native/build/Release/$probe.dll" -Destination "$work/payload/versioned-payload.dll" -Force
    Copy-Item -LiteralPath "$work/native/build/Release/WindowsIsland.exe" -Destination "$work/payload/WindowsIsland.exe" -Force
    & "$work/packaging/Build-Msi.ps1" -Version $version -PublishDirectory "$work/payload" -OutputDirectory "$work/distribution" -Wix $wixPath -InstallerActions "$work/native/build/Release/UpgradeActions.dll"
    if ($LASTEXITCODE) { throw 'Upgrade verification packaging failed.' }
    Join-Path $work "distribution/WindowsIsland-$version-win-x64.msi"
}
$old = @(Build-Package '0.1.0-rc.4' $legacy.OuterXml 'legacy')[-1]
$new = @(Build-Package '0.1.0' $definition 'current')[-1]
$brokenDefinition = $definition.Replace('<InstallExecuteSequence>', @'
<CustomAction Id="FailUpgrade" BinaryRef="InstallerActions" DllEntry="FailUpgrade" Execute="deferred" Impersonate="yes" Return="check" />
<InstallExecuteSequence>
  <Custom Action="FailUpgrade" After="InstallFiles" Condition="WIX_UPGRADE_DETECTED" />
'@)
$broken = @(Build-Package '0.1.0-testfailure' $brokenDefinition 'failed')[-1]
function Invoke-Msi([string]$mode, [string]$package, [string]$log, [switch]$Fail) {
    $process = Start-Process msiexec.exe -ArgumentList @($mode, ('"{0}"' -f $package), '/qn', '/norestart', ('INSTALLFOLDER="{0}"' -f $install), '/L*v', ('"{0}"' -f (Join-Path $work ($log + '.log')))) -Wait -PassThru -WindowStyle Hidden
    if ($Fail) { if ($process.ExitCode -ne 1603) { throw "Expected rollback failure: $log = $($process.ExitCode)" } }
    elseif ($process.ExitCode -notin @(0, 3010)) { throw "MSI check failed: $log = $($process.ExitCode)" }
}
function Start-RunningFixture([string]$directory, [switch]$DenyTerminate, [switch]$IgnoreExit) {
    $readyName = "Local\WindowsIsland.MsiShutdownVerification.$([Guid]::NewGuid().ToString('N'))"
    $ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $readyName)
    $previousReady = $env:WINDOWS_ISLAND_FIXTURE_READY
    $previousDeny = $env:WINDOWS_ISLAND_FIXTURE_DENY_TERMINATE
    $previousIgnore = $env:WINDOWS_ISLAND_FIXTURE_IGNORE_EXIT
    $process = $null
    try {
        $env:WINDOWS_ISLAND_FIXTURE_READY = $readyName
        $env:WINDOWS_ISLAND_FIXTURE_DENY_TERMINATE = if ($DenyTerminate) { '1' } else { '' }
        $env:WINDOWS_ISLAND_FIXTURE_IGNORE_EXIT = if ($IgnoreExit) { '1' } else { '' }
        $process = Start-Process (Join-Path $directory 'WindowsIsland.exe') -WorkingDirectory $directory -PassThru -WindowStyle Hidden
        if (!$ready.WaitOne(10000) -or $process.HasExited) { throw 'The isolated running-application fixture did not become ready.' }
        if ($DenyTerminate -and ![MsiShutdownFixture]::TerminationDenied($process.Id)) {
            Write-Warning 'The runner can bypass the fixture PROCESS_TERMINATE denial; graceful-close behavior will still be verified.'
        }
        return $process
    } catch {
        if ($process) { Stop-RunningFixture $process; $process.Dispose() }
        throw
    } finally {
        $env:WINDOWS_ISLAND_FIXTURE_READY = $previousReady
        $env:WINDOWS_ISLAND_FIXTURE_DENY_TERMINATE = $previousDeny
        $env:WINDOWS_ISLAND_FIXTURE_IGNORE_EXIT = $previousIgnore
        $ready.Dispose()
    }
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MsiShutdownFixture {
    private delegate bool EnumCallback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static bool TerminationDenied(int process) {
        var handle = OpenProcess(1, false, process);
        if (handle != IntPtr.Zero) { CloseHandle(handle); return false; }
        return Marshal.GetLastWin32Error() == 5;
    }
    public static void RequestExit(int process) {
        var message = RegisterWindowMessage("WindowsIsland.ExitForMaintenance");
        EnumWindows((window, _) => {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == process) PostMessage(window, message, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }
}
'@
function Stop-RunningFixture([Diagnostics.Process]$process) {
    if (!$process -or $process.HasExited) { return }
    if (!$process.Path.StartsWith($work + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to close a process outside the isolated fixture directory.'
    }
    [MsiShutdownFixture]::RequestExit($process.Id)
    if (!$process.WaitForExit(5000)) {
        Stop-Process -InputObject $process -Force
        if (!$process.WaitForExit(5000)) { throw 'The isolated running-application fixture did not stop.' }
    }
}
$beforeProbe = "$work/native/build/Release/ProbeBefore.dll"
$afterProbe = "$work/native/build/Release/ProbeAfter.dll"
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($beforeProbe).FileVersion -ne [Diagnostics.FileVersionInfo]::GetVersionInfo($afterProbe).FileVersion -or
    (Get-FileHash $beforeProbe).Hash -eq (Get-FileHash $afterProbe).Hash) { throw 'Versioned fixtures must have equal file versions and different contents.' }
$run = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runKey)
$approval = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($approvalKey)
$command = '"' + (Join-Path $install 'WindowsIsland.exe') + '" --startup'
$foreign = '"' + (Join-Path $work 'other/WindowsIsland.exe') + '" --startup'
try {
    foreach ($case in $Cases) {
        $running = $null
        $otherRunning = $null
        $run.DeleteValue('WindowsIsland', $false)
        $approval.DeleteValue('WindowsIsland', $false)
        if ($case -eq 'running-install') {
            $otherDirectory = Join-Path $work 'other running application'
            New-Item -ItemType Directory -Path $install,$otherDirectory -Force | Out-Null
            foreach ($target in @($install, $otherDirectory)) {
                Copy-Item -LiteralPath "$work/native/build/Release/WindowsIsland.exe" -Destination (Join-Path $target 'WindowsIsland.exe') -Force
                Copy-Item -LiteralPath $beforeProbe -Destination (Join-Path $target 'versioned-payload.dll') -Force
            }
            $running = Start-RunningFixture $install -DenyTerminate
            $otherRunning = Start-RunningFixture $otherDirectory
            Invoke-Msi '/i' $new "$case-install"
            if (!$running.HasExited -or $running.ExitCode -ne 0) { throw 'First installation did not gracefully close the exact-directory application without termination rights.' }
            if ($otherRunning.HasExited) { throw 'First installation closed an application in another directory.' }
            $running.Dispose()
            $running = $null
            Invoke-Msi '/x' $new "$case-uninstall"
            if ($otherRunning.HasExited) { throw 'Remove closed an application in another directory.' }
            Stop-RunningFixture $otherRunning
            $otherRunning.Dispose()
            $otherRunning = $null
            'PASS first installation gracefully closes an occupied exact directory without termination rights and preserves another directory.'
            continue
        }
        Invoke-Msi '/i' $old "$case-install"
        if ($case -ne 'missing') {
            $run.SetValue('WindowsIsland', $(if ($case -eq 'foreign') { $foreign } else { $command }))
            $state = [byte[]]@(2, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8)
            if ($case -in @('blocked', 'rollback')) { $state[0] = 3 }
            $approval.SetValue('WindowsIsland', $state, [Microsoft.Win32.RegistryValueKind]::Binary)
        }
        $expectedRun = $run.GetValue('WindowsIsland', $null)
        $expectedApproval = $approval.GetValue('WindowsIsland', $null)
        if ($case -in @('enabled', 'unresponsive')) {
            $running = Start-RunningFixture $install -DenyTerminate:($case -eq 'enabled') -IgnoreExit:($case -eq 'unresponsive')
        }
        if ($case -eq 'rollback') { Invoke-Msi '/i' $broken "$case-upgrade" -Fail }
        else { Invoke-Msi '/i' $new "$case-upgrade" }
        if ($running) {
            $running.Refresh()
            if (!$running.HasExited) { throw 'The MSI upgrade did not close the application that occupied installed files.' }
            $expectedExit = if ($case -eq 'unresponsive') { 1603 } else { 0 }
            if ($running.ExitCode -ne $expectedExit) { throw 'The MSI upgrade did not use the expected graceful or bounded forced exit.' }
            $running.Dispose()
            $running = $null
        }
        if ($run.GetValue('WindowsIsland', $null) -cne $expectedRun) { throw "Startup command changed: $case" }
        $actualApproval = $approval.GetValue('WindowsIsland', $null)
        if (($null -eq $actualApproval) -ne ($null -eq $expectedApproval)) { throw "Startup approval presence changed: $case" }
        if ([Convert]::ToHexString([byte[]]@($actualApproval)) -cne [Convert]::ToHexString([byte[]]@($expectedApproval))) { throw "Startup approval changed: $case" }
        if ($null -ne $expectedApproval -and $approval.GetValueKind('WindowsIsland') -ne 'Binary') { throw "Approval type changed: $case" }
        $expectedMarker = if ($case -eq 'rollback') { 'legacy' } else { 'current' }
        if ([IO.File]::ReadAllText((Join-Path $install 'upgrade-marker.txt')) -cne $expectedMarker) { throw "Installed version is incorrect: $case" }
        $expectedProbe = if ($case -eq 'rollback') { $beforeProbe } else { $afterProbe }
        if ((Get-FileHash -LiteralPath (Join-Path $install 'versioned-payload.dll')).Hash -ne (Get-FileHash -LiteralPath $expectedProbe).Hash) {
            throw "Equal-version binary was not replaced or rolled back: $case"
        }
        foreach ($file in @(Get-ChildItem -LiteralPath $publish -File -Recurse)) {
            $relative = [IO.Path]::GetRelativePath($publish, $file.FullName)
            $expected = if ($relative -eq 'WindowsIsland.exe') { "$work/native/build/Release/WindowsIsland.exe" } else { $file.FullName }
            if ((Get-FileHash -LiteralPath (Join-Path $install $relative)).Hash -ne (Get-FileHash -LiteralPath $expected).Hash) {
                throw "Installed payload hash mismatch: $case / $relative"
            }
        }
        if ($case -eq 'enabled') {
            $legacyHook = Join-Path $install ('Hooks/' + ('A' * 64))
            New-Item -ItemType Directory -Path $legacyHook,(Join-Path $install 'microsoft.system.package.metadata') -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $legacyHook 'WindowsIsland.TelegramHook.dll'), 'legacy runtime copy')
            [IO.File]::WriteAllText((Join-Path $install 'WindowsIsland.TelegramHook.dll'), 'legacy root runtime copy')
            [IO.File]::WriteAllText((Join-Path $install '.update-files.json'), '[]')
            $running = Start-RunningFixture $install -DenyTerminate
        }
        Invoke-Msi '/x' $(if ($case -eq 'rollback') { $old } else { $new }) "$case-uninstall"
        if ($running) {
            $running.Refresh()
            if (!$running.HasExited) { throw 'MSI Remove did not close the running application.' }
            if ($running.ExitCode -ne 0) { throw 'MSI Remove terminated the application instead of requesting a graceful exit.' }
            $running.Dispose()
            $running = $null
        }
        if ($case -eq 'foreign') {
            if ($run.GetValue('WindowsIsland') -cne $foreign) { throw 'Uninstall affected another installation.' }
        } elseif ($null -ne $run.GetValue('WindowsIsland', $null)) { throw "Uninstall retained its startup command: $case" }
        if (Test-Path (Join-Path $install 'WindowsIsland.exe')) { throw "Uninstall retained its executable: $case" }
        if (Test-Path (Join-Path $install 'WindowsIsland.TelegramHook.dll')) { throw "Uninstall retained its Telegram hook: $case" }
        if ($case -eq 'enabled' -and (Test-Path -LiteralPath $install)) { throw 'MSI Remove retained its managed installation directory.' }
        "PASS isolated MSI upgrade preserves startup and installation state: $case"
        if ($case -eq 'enabled') { 'PASS isolated MSI upgrade and Remove close occupied files and remove managed runtime content.' }
        if ($case -eq 'unresponsive') { 'PASS isolated MSI upgrade terminates only its unresponsive exact-directory fixture after the graceful-exit timeout.' }
    }
} catch {
    $_ | Out-String | Set-Content (Join-Path $work 'failure.txt')
    throw
} finally {
    if ($otherRunning) {
        Stop-RunningFixture $otherRunning
        $otherRunning.Dispose()
    }
    if ($running) { Stop-RunningFixture $running }
    if ($running) { $running.Dispose() }
    foreach ($package in @($old, $new, $broken)) {
        $process = Start-Process msiexec.exe -ArgumentList @('/x', ('"{0}"' -f $package), '/qn', '/norestart', ('INSTALLFOLDER="{0}"' -f $install)) -Wait -PassThru -WindowStyle Hidden
        if ($process.ExitCode -notin @(0, 3010, 1605, 1614)) { Write-Warning "Isolated package cleanup failed: $package ($($process.ExitCode))" }
    }
    $run.Dispose(); $approval.Dispose()
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($scope, $false)
}
if ((Get-ItemPropertyValue 'HKCU:\Software\Cloudwhile\WindowsIsland\Installer' -Name InstallDirectory -ErrorAction SilentlyContinue) -cne $normalInstall) { throw 'The normal installation changed.' }
foreach ($process in $normalProcesses) {
    if ((Get-Process -Id $process.Id -ErrorAction Stop).Path -cne $process.Path) { throw 'An existing application process changed.' }
}
'PASS the normal installation and pre-existing application processes remain unchanged.'
