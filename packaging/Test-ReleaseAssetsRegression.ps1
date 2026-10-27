#Requires -Version 7.0
param()
$ErrorActionPreference = 'Stop'
$work = Join-Path $PSScriptRoot ('../artifacts/verification/release-assets/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$version = '0.1.0'
$checker = Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1'
$checks = [Collections.Generic.List[string]]::new()
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Archive([string]$path, [Collections.IDictionary]$entries, [string]$duplicate = '') {
    $file = [IO.File]::Open($path, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @($entries.Keys) + @($duplicate | Where-Object { $_ })) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write([string]$entries[$name]) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose(); $file.Dispose() }
}
function Fixture([string]$root, [string]$case) {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $root "WindowsIsland-$version-win-x64.exe"), 'fixture')
    Archive (Join-Path $root "WindowsIsland-$version-win-x64.zip") @{'WindowsIsland.dll'='fixture'}
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
foreach ($case in @('valid','missing-sdk','extra','sdk-version','cli-version','cli-entry','worker-version','worker-missing','duplicate','sample-version','digest','digest-name')) {
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
