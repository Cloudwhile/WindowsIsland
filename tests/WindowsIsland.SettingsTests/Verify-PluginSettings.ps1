$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runRoot = Join-Path $repoRoot ('artifacts/verification/plugin-settings-ui/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$appSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src/WindowsIsland/App.xaml.cs') -Raw -Encoding utf8
$needle = 'if (_window is not null) { _window.OpenSettings(); return; }'
if (-not $appSource.Contains($needle)) { throw 'Verification launch insertion point was not found.' }
$appSource = $appSource.Replace($needle, 'await PluginSettingsUiChecks.RunAsync(this); return;')
[IO.File]::WriteAllText((Join-Path $runRoot 'App.xaml.cs'), $appSource)
$targets = @"
<Project>
  <ItemGroup Condition="'`$(MSBuildProjectName)' == 'WindowsIsland'">
    <Compile Remove="App.xaml.cs" />
    <Compile Include="$runRoot/App.xaml.cs" />
    <Compile Include="$PSScriptRoot/PluginSettingsUiChecks.cs" />
  </ItemGroup>
</Project>
"@
$targetsPath = Join-Path $runRoot 'UiChecks.targets'
[IO.File]::WriteAllText($targetsPath, $targets)
Push-Location $repoRoot
try {
    dotnet build tests/WindowsIsland.PluginFixture/WindowsIsland.PluginFixture.csproj -c Release --no-restore *> (Join-Path $runRoot 'fixture-build.log')
    if ($LASTEXITCODE -ne 0) { throw "Fixture build failed: $runRoot/fixture-build.log" }
    dotnet build src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64 --no-restore "-p:CustomAfterMicrosoftCommonTargets=$targetsPath" "-p:OutDir=$runRoot/app/" *> (Join-Path $runRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $runRoot 'build.log') -Tail 20; throw 'UI verification build failed.' }
    New-Item -ItemType Directory -Path (Join-Path $runRoot 'app/fixture') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'artifacts/bin/WindowsIsland.PluginFixture/Release/net10.0/WindowsIsland.PluginFixture.dll') -Destination (Join-Path $runRoot 'app/fixture')
    $process = Start-Process -FilePath (Join-Path $runRoot 'app/WindowsIsland.exe') -ArgumentList '--verify-local', '--verify-light' -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw "UI verification timed out: $runRoot" }
    $failure = Join-Path $runRoot 'app/plugin-settings-ui.error'
    if (Test-Path -LiteralPath $failure) { throw (Get-Content -LiteralPath $failure -Raw) }
    if (-not (Test-Path -LiteralPath (Join-Path $runRoot 'app/plugin-settings-ui.success'))) { throw "UI verification did not complete: $runRoot" }
    Get-Content -LiteralPath (Join-Path $runRoot 'app/plugin-settings-ui.log')
    Write-Output "Verification output: $runRoot"
} finally { Pop-Location }
