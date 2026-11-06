param(
    [Parameter(Mandatory=$true)][string]$CliPath,
    [Parameter(Mandatory=$true)][string]$OutputRoot
)
$ErrorActionPreference='Stop'
$repository=Split-Path $PSScriptRoot -Parent
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $OutputRoot) {throw 'Use a new isolated regression output root.'}
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$checks=[Collections.Generic.List[string]]::new()
function Assert($condition,[string]$name) {
    if(!$condition) {throw "FAIL: $name"}
    $checks.Add($name)
    Write-Output "PASS: $name"
}
function Write-Json([string]$path,$value) {
    [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class CliRegressionProfile {
 [DllImport("userenv.dll",CharSet=CharSet.Unicode)] public static extern int DeriveAppContainerSidFromAppContainerName(string name,out IntPtr sid);
 [DllImport("advapi32.dll")] public static extern IntPtr FreeSid(IntPtr sid);
}
"@
foreach($mode in @('wrong-source','no-permission','invalid-pinned','throw-start','valid','delayed')) {
    $project=Join-Path $OutputRoot $mode
    New-Item -ItemType Directory -Path "$project/src","$project/sdk" | Out-Null
    Copy-Item "$repository/tests/WindowsIsland.PluginCliFixture/ReviewSource.cs" "$project/src/Plugin.cs"
    Copy-Item "$repository/artifacts/bin/WindowsIsland.Contracts/Release/net10.0/WindowsIsland.Contracts.dll" "$project/sdk/WindowsIsland.Contracts.dll"
    @'
<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems><AssemblyName>Fixture</AssemblyName></PropertyGroup>
 <ItemGroup><Compile Include="src/Plugin.cs"/><Reference Include="WindowsIsland.Contracts"><HintPath>sdk/WindowsIsland.Contracts.dll</HintPath><Private>false</Private></Reference></ItemGroup>
</Project>
'@ | Set-Content "$project/Fixture.csproj" -Encoding UTF8
    $permissions=if($mode -eq 'no-permission') {@()} elseif($mode -in @('valid','delayed')) {@('notifications','custom_capability')} else {@('notifications')}
    Write-Json "$project/manifest.json" @{id='wini_plugin_cli_regression';name='CLI regression';version='1.0.0';entryAssembly='Fixture.dll';entryType='WindowsIsland.PluginCliFixture.ReviewSource';contractVersion=1;permissions=@($permissions)}
    $success=$mode -in @('valid','delayed')
    $count=if($success) {1} else {0}
    Write-Json "$project/plugin.test.json" @{schemaVersion=1;scenarios=@(@{waitMilliseconds=600;minimumNotifications=$count;maximumNotifications=$count;settings=@{mode=$mode}})}
    # 预期错误不能被 PowerShell 5.1 的原生 stderr 行为提前截断。
    $previousPreference=$ErrorActionPreference
    $ErrorActionPreference='Continue'
    & $CliPath test $project 2>&1 | ForEach-Object {Write-Output $_.ToString()}
    $code=$LASTEXITCODE
    $ErrorActionPreference=$previousPreference
    Assert (($success -and $code -eq 0) -or (!$success -and $code -ne 0)) "$mode has the expected test exit code"
    $test=@(Get-ChildItem "$project/.wini/tests" -Directory)
    Assert ($test.Count -eq 1) "$mode emits one isolated test result"
    $cleanup=Get-Content "$($test[0].FullName)/cleanup.json" -Raw | ConvertFrom-Json
    Assert ($cleanup.profileRemoved -and $cleanup.allWorkersExited) "$mode confirms worker exit and container cleanup"
    $sha=[Security.Cryptography.SHA256]::Create()
    try {$identity=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($test[0].FullName.ToUpperInvariant()+"`n"+'wini_plugin_cli_regression'))).Replace('-','').Substring(0,40).ToLowerInvariant()}
    finally {$sha.Dispose()}
    $sidPointer=[IntPtr]::Zero
    [Runtime.InteropServices.Marshal]::ThrowExceptionForHR([CliRegressionProfile]::DeriveAppContainerSidFromAppContainerName('WindowsIsland.Plugin.'+$identity,[ref]$sidPointer))
    try {$sid=[Security.Principal.SecurityIdentifier]::new($sidPointer).Value} finally {[CliRegressionProfile]::FreeSid($sidPointer) | Out-Null}
    $mapping='HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings\'+$sid
    Assert (!(Test-Path -LiteralPath $mapping)) "$mode leaves no registered AppContainer profile"
}
$checks | ConvertTo-Json | Set-Content "$OutputRoot/checks.json" -Encoding UTF8
Write-Output "$($checks.Count) CLI regression checks passed."
$global:LASTEXITCODE=0
