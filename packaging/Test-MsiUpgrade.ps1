param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'),
    [string]$Wix = (Join-Path $PSScriptRoot '../artifacts/tools/wix/wix.exe'),
    [ValidateSet('enabled', 'blocked', 'missing', 'foreign', 'rollback')]
    [string[]]$Cases = @('enabled', 'blocked', 'missing', 'foreign', 'rollback')
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
foreach ($node in @($legacy.SelectNodes('//w:Binary|//w:CustomAction[@Id="SelectInstallScope" or @Id="CaptureStartup" or @Id="RestoreStartup" or @Id="RollbackStartup"]|//w:Property[@Id="RestoreStartup" or @Id="RollbackStartup" or @Id="REINSTALLMODE"]|//w:Custom[@Action="SelectInstallScope" or @Action="CaptureStartup" or @Action="RestoreStartup" or @Action="RollbackStartup"]|//w:Publish[@Value="SelectInstallScope"]', $ns))) {
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
        $run.DeleteValue('WindowsIsland', $false)
        $approval.DeleteValue('WindowsIsland', $false)
        Invoke-Msi '/i' $old "$case-install"
        if ($case -ne 'missing') {
            $run.SetValue('WindowsIsland', $(if ($case -eq 'foreign') { $foreign } else { $command }))
            $state = [byte[]]@(2, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8)
            if ($case -in @('blocked', 'rollback')) { $state[0] = 3 }
            $approval.SetValue('WindowsIsland', $state, [Microsoft.Win32.RegistryValueKind]::Binary)
        }
        $expectedRun = $run.GetValue('WindowsIsland', $null)
        $expectedApproval = $approval.GetValue('WindowsIsland', $null)
        if ($case -eq 'rollback') { Invoke-Msi '/i' $broken "$case-upgrade" -Fail }
        else { Invoke-Msi '/i' $new "$case-upgrade" }
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
            if ((Get-FileHash -LiteralPath (Join-Path $install $relative)).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                throw "Installed payload hash mismatch: $case / $relative"
            }
        }
        Invoke-Msi '/x' $(if ($case -eq 'rollback') { $old } else { $new }) "$case-uninstall"
        if ($case -eq 'foreign') {
            if ($run.GetValue('WindowsIsland') -cne $foreign) { throw 'Uninstall affected another installation.' }
        } elseif ($null -ne $run.GetValue('WindowsIsland', $null)) { throw "Uninstall retained its startup command: $case" }
        if (Test-Path (Join-Path $install 'WindowsIsland.exe')) { throw "Uninstall retained its executable: $case" }
        "PASS isolated MSI upgrade preserves startup and installation state: $case"
    }
} catch {
    $_ | Out-String | Set-Content (Join-Path $work 'failure.txt')
    throw
} finally {
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
