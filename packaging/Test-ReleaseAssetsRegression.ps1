#Requires -Version 7.0
param()
$ErrorActionPreference = 'Stop'
$work = Join-Path $PSScriptRoot ('../artifacts/verification/release-assets/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$version = '0.1.0'
$checker = Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1'
$checks = [Collections.Generic.List[string]]::new()
Add-Type -AssemblyName System.IO.Compression.FileSystem
$products = @{}
foreach ($productVersion in @($version, '0.2.0')) {
    $projectRoot = Join-Path $work ('pe-' + $productVersion)
    New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
    # Add-Type does not reliably emit Win32 VERSIONINFO; build a tiny SDK assembly instead.
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>$productVersion</Version>
    <InformationalVersion>v$productVersion+fixture</InformationalVersion>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
  </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $projectRoot 'Fixture.csproj') -Encoding UTF8
    'internal static class Marker;' | Set-Content -LiteralPath (Join-Path $projectRoot 'Marker.cs') -Encoding UTF8
    $output = Join-Path $projectRoot 'out'
    dotnet build (Join-Path $projectRoot 'Fixture.csproj') -c Release -o $output --disable-build-servers | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not build versioned release fixture $productVersion." }
    $binary = Join-Path $output 'Fixture.dll'
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary)
    if (($info.ProductVersion ?? '').Split('+')[0].TrimStart('v') -cne $productVersion) {
        throw "Versioned release fixture missing ProductVersion $productVersion."
    }
    $products[$productVersion] = [IO.File]::ReadAllBytes($binary)
}
function Archive([string]$path, [Collections.IDictionary]$entries, [string]$duplicate = '') {
    $file = [IO.File]::Open($path, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @($entries.Keys) + @($duplicate | Where-Object { $_ })) {
            $stream = $zip.CreateEntry($name).Open()
            try {
                if ($entries[$name] -is [byte[]]) { $bytes = $entries[$name] }
                else { $bytes = [Text.UTF8Encoding]::new($false).GetBytes([string]$entries[$name]) }
                $stream.Write($bytes, 0, $bytes.Length)
            } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose(); $file.Dispose() }
}
function Fixture([string]$root, [string]$case) {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $root "WindowsIsland-$version-win-x64.exe"), 'fixture')
    $application = @{}
    foreach ($name in @('WindowsIsland.deps.json','WindowsIsland.Contracts.dll','WindowsIsland.PluginIsolation.dll',
        'WindowsIsland.pri','App.xbf','Microsoft.UI.Xaml.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll',
        'System.Private.CoreLib.dll','PresentationFramework.dll','en-US/WindowsIsland.resources.dll','Assets/Island.ico','Icons/LOGO.png',
        'Assets/Shell/AppList.png','Assets/Shell/MediumTile.png','Assets/Shell/StoreLogo.png',
        'Initialization/AppxManifest.xml','Initialization/RuntimeManifest.xml','Unregister-Package.ps1',
        'LICENSE','README.md','README.zh-CN.md','Licenses/Markdig.txt')) { $application[$name] = 'fixture' }
    foreach ($name in @('WindowsIsland.exe','WindowsIsland.dll')) { $application[$name] = $products[$version] }
    foreach ($name in @('WindowsIsland.PluginWorker.exe','WindowsIsland.PluginWorker.dll','WindowsIsland.PluginWorker.deps.json',
        'WindowsIsland.Contracts.dll','WindowsIsland.PluginIsolation.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll')) {
        $application['PluginWorker/' + $name] = 'fixture'
    }
    $application['portable.marker'] = ''
    $hostRuntime = if ($case -eq 'app-runtime-version') { '10.0.13' } else { '10.0.14' }
    $application['WindowsIsland.runtimeconfig.json'] = '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"' + $hostRuntime + '"},{"name":"Microsoft.WindowsDesktop.App","version":"10.0.14"}]}}'
    $appWorkerRuntime = if ($case -eq 'app-worker-version') { '10.0.13' } else { '10.0.14' }
    $application['PluginWorker/WindowsIsland.PluginWorker.runtimeconfig.json'] = '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"' + $appWorkerRuntime + '"}]}}'
    if ($case -eq 'app-exe-version') { $application['WindowsIsland.exe'] = $products['0.2.0'] }
    if ($case -eq 'app-dll-version') { $application['WindowsIsland.dll'] = $products['0.2.0'] }
    if ($case -eq 'app-version-missing') { $application['WindowsIsland.dll'] = 'fixture' }
    if ($case -eq 'app-marker') { $application.Remove('portable.marker') }
    if ($case -eq 'app-worker') { $application.Remove('PluginWorker/coreclr.dll') }
    if ($case -eq 'app-worker-config') { $application.Remove('PluginWorker/WindowsIsland.PluginWorker.runtimeconfig.json') }
    if ($case -eq 'app-init') { $application.Remove('Initialization/AppxManifest.xml') }
    if ($case -eq 'app-init-runtime') { $application.Remove('Initialization/RuntimeManifest.xml') }
    if ($case -eq 'app-empty') { $application['App.xbf'] = '' }
    if ($case -eq 'app-hook') { $application['WindowsIsland.TelegramHook.dll'] = 'fixture' }
    $appDuplicate = if ($case -eq 'app-duplicate') { 'WindowsIsland.dll' } else { '' }
    Archive (Join-Path $root "WindowsIsland-$version-win-x64.zip") $application $appDuplicate
    $sdkVersion = if ($case -eq 'sdk-version') { '0.2.0' } else { $version }
    Archive (Join-Path $root "WindowsIsland.Contracts.$version.nupkg") @{
        'WindowsIsland.Contracts.nuspec'="<package><metadata><id>WindowsIsland.Contracts</id><version>$sdkVersion</version></metadata></package>"
        'lib/net10.0/WindowsIsland.Contracts.dll'='fixture'
    }
    $cliVersion = if ($case -eq 'cli-version') { '0.2.0' } else { $version }
    $runtimeVersion = if ($case -eq 'worker-version') { '10.0.13' } else { '10.0.14' }
    $entries = @{'WindowsIsland.Plugin.Cli.nuspec'="<package><metadata><id>WindowsIsland.Plugin.Cli</id><version>$cliVersion</version></metadata></package>"}
    foreach ($name in @('WindowsIsland.Plugin.Cli.dll','WindowsIsland.Plugin.Cli.deps.json','WindowsIsland.Plugin.Cli.runtimeconfig.json',
        'DotnetToolSettings.xml','WindowsIsland.Contracts.dll','WindowsIsland.PluginIsolation.dll')) { $entries['tools/net10.0/any/' + $name]='fixture' }
    foreach ($name in @('WindowsIsland.PluginWorker.exe','WindowsIsland.PluginWorker.dll','WindowsIsland.Contracts.dll','WindowsIsland.PluginIsolation.dll',
        'coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll')) { $entries['tools/net10.0/any/PluginWorker/' + $name]='fixture' }
    $entries['tools/net10.0/any/PluginWorker/WindowsIsland.PluginWorker.runtimeconfig.json'] =
        '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"' + $runtimeVersion + '"}]}}'
    if ($case -eq 'cli-entry') { $entries.Remove('tools/net10.0/any/WindowsIsland.Plugin.Cli.dll') }
    if ($case -eq 'worker-missing') { $entries.Remove('tools/net10.0/any/PluginWorker/coreclr.dll') }
    $duplicate = if ($case -eq 'duplicate') { 'tools/net10.0/any/PluginWorker/coreclr.dll' } else { '' }
    Archive (Join-Path $root "WindowsIsland.Plugin.Cli.$version.nupkg") $entries $duplicate
    $sampleVersion = if ($case -eq 'sample-version') { '0.2.0' } else { $version }
    Archive (Join-Path $root "WindowsIsland.Plugin.Samples.$version.zip") @{
        'Example/Example.csproj'="<Project><ItemGroup><PackageReference Include=`"WindowsIsland.Contracts`" Version=`"$sampleVersion`"/></ItemGroup></Project>"
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -File) {
        $hash = (Get-FileHash $file.FullName).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText(($file.FullName + '.sha256'), "$hash  $($file.Name)`n")
    }
    if ($case -eq 'missing-sdk') { Remove-Item -LiteralPath (Join-Path $root "WindowsIsland.Contracts.$version.nupkg") }
    if ($case -eq 'extra') { [IO.File]::WriteAllText((Join-Path $root 'unexpected.txt'), 'fixture') }
    if ($case -eq 'digest') { [IO.File]::AppendAllText((Join-Path $root "WindowsIsland.Contracts.$version.nupkg"), 'changed') }
    if ($case -eq 'digest-name') {
        $file = Join-Path $root "WindowsIsland.Contracts.$version.nupkg"
        [IO.File]::WriteAllText(($file + '.sha256'), (Get-FileHash $file).Hash.ToLowerInvariant() + '  other.nupkg')
    }
}
foreach ($case in @('valid','app-exe-version','app-dll-version','app-version-missing','app-marker','app-worker','app-worker-config',
    'app-init','app-init-runtime','app-empty','app-hook','app-duplicate','app-runtime-version','app-worker-version',
    'missing-sdk','extra','sdk-version','cli-version','cli-entry','worker-version','worker-missing','duplicate','sample-version','digest','digest-name')) {
    $root = Join-Path $work $case
    Fixture $root $case
    $accepted = $false
    try { & $checker -Directory $root -Version $version *> (Join-Path $work ($case + '.log')); $accepted = $true }
    catch { $_ | Out-String | Add-Content (Join-Path $work ($case + '.log')) }
    if ($accepted -ne ($case -eq 'valid')) { throw "Release asset regression failed: $case" }
    $checks.Add($case)
    "PASS release asset validation: $case"
}
$checks | ConvertTo-Json | Set-Content (Join-Path $work 'checks.json')
'Verification output: ' + $work
