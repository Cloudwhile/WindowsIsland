#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Version,
    [string]$RuntimeVersion = '10.0.14'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path
$names = @("WindowsIsland-$Version-win-x64.exe", "WindowsIsland-$Version-win-x64.zip",
    "WindowsIsland.Contracts.$Version.nupkg", "WindowsIsland.Plugin.Cli.$Version.nupkg", "WindowsIsland.Plugin.Samples.$Version.zip")
$expected = @($names) + @($names | ForEach-Object { $_ + '.sha256' })
$actual = @(Get-ChildItem -LiteralPath $root -File | ForEach-Object Name)
if (@(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)).Count) { throw 'Release assets are incomplete or contain unexpected files.' }
foreach ($name in $names) {
    $file = Join-Path $root $name
    if ((Get-Item -LiteralPath $file).Length -le 0) { throw "Empty release asset: $name" }
    $digest = [IO.File]::ReadAllText($file + '.sha256').Trim()
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($digest -cne "$hash  $name") { throw "Release checksum mismatch: $name" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Read-Entry($archive, [string]$name) {
    $entry = $archive.GetEntry($name)
    if (!$entry -or $entry.Length -gt 1048576) { throw "Missing or invalid package metadata: $name" }
    $reader = [IO.StreamReader]::new($entry.Open(), [Text.Encoding]::UTF8)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
foreach ($id in @('WindowsIsland.Contracts', 'WindowsIsland.Plugin.Cli')) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $root "$id.$Version.nupkg"))
    try {
        $paths = @($archive.Entries | ForEach-Object FullName)
        if (@($paths | Sort-Object -Unique).Count -ne $paths.Count) { throw "Duplicate package entries: $id" }
        $manifest = [xml](Read-Entry $archive "$id.nuspec")
        $metadata = $manifest.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        if ($metadata.id -cne $id -or $metadata.version -cne $Version) { throw "Package identity or version mismatch: $id" }
        if ($id -eq 'WindowsIsland.Contracts') {
            if (!$archive.GetEntry('lib/net10.0/WindowsIsland.Contracts.dll')) { throw 'Contracts SDK assembly is missing.' }
        } else {
            foreach ($file in @('WindowsIsland.Plugin.Cli.dll','WindowsIsland.Plugin.Cli.deps.json','WindowsIsland.Plugin.Cli.runtimeconfig.json',
                'DotnetToolSettings.xml','WindowsIsland.Contracts.dll','WindowsIsland.PluginIsolation.dll')) {
                if (!$archive.GetEntry('tools/net10.0/any/' + $file)) { throw "CLI tool payload is incomplete: $file" }
            }
            $prefix = 'tools/net10.0/any/PluginWorker/'
            foreach ($file in @('WindowsIsland.PluginWorker.exe','WindowsIsland.PluginWorker.dll','WindowsIsland.Contracts.dll',
                'WindowsIsland.PluginIsolation.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll')) {
                if (!$archive.GetEntry($prefix + $file)) { throw "CLI isolated runtime is incomplete: $file" }
            }
            $runtime = Read-Entry $archive ($prefix + 'WindowsIsland.PluginWorker.runtimeconfig.json') | ConvertFrom-Json
            $framework = @($runtime.runtimeOptions.includedFrameworks)
            if ($framework.Count -ne 1 -or $framework[0].name -ne 'Microsoft.NETCore.App' -or $framework[0].version -ne $RuntimeVersion) {
                throw 'CLI isolated runtime version does not match the release baseline.'
            }
        }
    } finally { $archive.Dispose() }
}
$samples = [IO.Compression.ZipFile]::OpenRead((Join-Path $root "WindowsIsland.Plugin.Samples.$Version.zip"))
try {
    $projects = @($samples.Entries | Where-Object FullName -Like '*.csproj')
    if ($projects.Count -eq 0) { throw 'Release samples contain no projects.' }
    foreach ($project in $projects) {
        $definition = [xml](Read-Entry $samples $project.FullName)
        $references = @($definition.SelectNodes('/Project/ItemGroup/PackageReference[@Include="WindowsIsland.Contracts"]'))
        if ($references.Count -ne 1 -or $references[0].Version -cne $Version) { throw 'Release sample dependency does not match the SDK package.' }
    }
} finally { $samples.Dispose() }
'PASS application, Contracts SDK, CLI and samples have matching versions, complete payloads and SHA-256 checksums.'
