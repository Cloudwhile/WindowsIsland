param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'),
    [string]$Project = (Join-Path $PSScriptRoot '../src/WindowsIsland/WindowsIsland.csproj')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (!(Test-Path -LiteralPath (Join-Path $publish 'WindowsIsland.TelegramHook.dll') -PathType Leaf)) {
    throw 'Published payload is missing WindowsIsland.TelegramHook.dll.'
}
$worker = Join-Path $publish 'PluginWorker'
foreach ($file in @('WindowsIsland.PluginWorker.exe', 'WindowsIsland.PluginWorker.dll', 'WindowsIsland.PluginWorker.deps.json',
    'WindowsIsland.PluginWorker.runtimeconfig.json', 'WindowsIsland.PluginIsolation.dll', 'WindowsIsland.Contracts.dll',
    'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $worker $file) -PathType Leaf)) { throw "Missing isolated plugin runtime: $file" }
}
foreach ($file in @('WindowsIsland.dll', 'WindowsIsland.TelegramHook.dll', 'Microsoft.UI.Xaml.dll', 'PresentationFramework.dll')) {
    if (Test-Path -LiteralPath (Join-Path $worker $file)) { throw "Isolated plugin runtime includes a host dependency: $file" }
}
'PASS Isolated plugin runtime is self-contained and excludes host desktop dependencies.'
$definition = [xml](Get-Content -LiteralPath $Project -Raw)
$references = @($definition.SelectNodes('/Project/ItemGroup/FrameworkReference'))
$expected = @{}
foreach ($reference in $references) {
    $identity = $reference.GetAttribute('Include')
    if (!$identity) { $identity = $reference.GetAttribute('Update') }
    if ($identity -eq 'Microsoft.WindowsDesktop.App.WPF') { $identity = 'Microsoft.WindowsDesktop.App' }
    if ($identity -in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
        $version = $reference.GetAttribute('RuntimeFrameworkVersion')
        if (!$version) { throw 'A publish runtime baseline is missing.' }
        $expected[$identity] = $version
    }
}
if ($expected.Count -ne 2) { throw 'Both publish runtime baselines are required.' }
$resolved = & dotnet msbuild $Project -p:Configuration=Release -p:Platform=x64 -t:ResolveFrameworkReferences -getItem:ResolvedRuntimePack
if ($LASTEXITCODE) { throw 'Runtime pack resolution failed.' }
$packs = ($resolved -join "`n" | ConvertFrom-Json).Items.ResolvedRuntimePack
$runtime = Get-Content -LiteralPath (Join-Path $publish 'WindowsIsland.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($name in $expected.Keys) {
    $configuration = @($runtime.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq $name })
    if ($configuration.Count -ne 1 -or $configuration[0].version -ne $expected[$name]) { throw "Runtime configuration mismatch: $name" }
    $pack = @($packs | Where-Object { $_.FrameworkName -eq $name })
    if ($pack.Count -ne 1 -or $pack[0].NuGetPackageVersion -ne $expected[$name]) { throw "Resolved runtime pack mismatch: $name" }
    $files = if ($name -eq 'Microsoft.NETCore.App') {
        @('runtimes/win-x64/native/coreclr.dll','runtimes/win-x64/lib/net10.0/System.Private.CoreLib.dll')
    } else {
        @('runtimes/win-x64/lib/net10.0/PresentationFramework.dll','runtimes/win-x64/lib/net10.0/PresentationCore.dll',
          'runtimes/win-x64/lib/net10.0/UIAutomationClient.dll','runtimes/win-x64/lib/net10.0/WindowsBase.dll')
    }
    foreach ($relative in $files) {
        $source = Join-Path $pack[0].PackageDirectory $relative
        $target = Join-Path $publish ([IO.Path]::GetFileName($relative))
        if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            throw "Published runtime file mismatch: $([IO.Path]::GetFileName($relative))"
        }
    }
    if ($name -eq 'Microsoft.NETCore.App') {
        $workerRuntime = Get-Content -LiteralPath (Join-Path $worker 'WindowsIsland.PluginWorker.runtimeconfig.json') -Raw | ConvertFrom-Json
        $workerFrameworks = @($workerRuntime.runtimeOptions.includedFrameworks)
        if ($workerFrameworks.Count -ne 1 -or $workerFrameworks[0].name -ne $name -or $workerFrameworks[0].version -ne $expected[$name]) {
            throw 'Isolated plugin runtime configuration does not match the host runtime baseline.'
        }
        foreach ($relative in @('runtimes/win-x64/native/coreclr.dll','runtimes/win-x64/native/hostfxr.dll',
            'runtimes/win-x64/native/hostpolicy.dll','runtimes/win-x64/lib/net10.0/System.Private.CoreLib.dll')) {
            $source = Join-Path $pack[0].PackageDirectory $relative
            $target = Join-Path $worker ([IO.Path]::GetFileName($relative))
            if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
                throw "Isolated plugin runtime file mismatch: $([IO.Path]::GetFileName($relative))"
            }
        }
        foreach ($assembly in @('WindowsIsland.Contracts.dll', 'WindowsIsland.PluginIsolation.dll')) {
            if ((Get-FileHash -LiteralPath (Join-Path $publish $assembly)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $worker $assembly)).Hash) {
                throw "Isolated plugin contract does not match the host: $assembly"
            }
        }
        'PASS Isolated plugin runtime configuration, runtime files and shared contracts match the host.'
    }
    "PASS $name configuration, resolved pack and published files match $($expected[$name])."
}
