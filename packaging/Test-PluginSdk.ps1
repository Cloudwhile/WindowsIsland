param([Parameter(Mandatory=$true)][string]$OutputRoot, [string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw 'Use a new SDK verification directory.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$feed = Join-Path $OutputRoot 'feed'
$toolPath = Join-Path $OutputRoot 'tool'
$checks = [Collections.Generic.List[string]]::new()
function Assert($condition, [string]$name) {
    if (!$condition) { throw "FAIL: $name" }
    $checks.Add($name)
    Write-Output "PASS: $name"
}
dotnet pack "$repository/src/WindowsIsland.Contracts/WindowsIsland.Contracts.csproj" -c Release "-p:Version=$Version" -o $feed --disable-build-servers
Assert ($LASTEXITCODE -eq 0) 'Build standalone Contracts package'
dotnet pack "$repository/src/WindowsIsland.Plugin.Cli/WindowsIsland.Plugin.Cli.csproj" -c Release "-p:Version=$Version" -o $feed --disable-build-servers
Assert ($LASTEXITCODE -eq 0) 'Build CLI package with worker runtime'
$config = Join-Path $OutputRoot 'NuGet.Config'
'<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content $config -Encoding UTF8
dotnet tool install WindowsIsland.Plugin.Cli --version $Version --tool-path $toolPath --add-source $feed --configfile $config
Assert ($LASTEXITCODE -eq 0) 'Install CLI from isolated local feed'
$cli = Join-Path $toolPath 'wini-plugin.exe'
$projects = Join-Path $OutputRoot 'projects'
New-Item -ItemType Directory -Path $projects | Out-Null
Push-Location $projects
try {
    foreach ($template in @('notification', 'pinned', 'network')) {
        $name = 'Sample' + $template
        & $cli init $name --template $template
        Assert ($LASTEXITCODE -eq 0) "Generate $template project and test plan"
        & $cli validate $name
        Assert ($LASTEXITCODE -eq 0) "Validate $template"
        & $cli test $name
        Assert ($LASTEXITCODE -eq 0) "Run $template isolated functional scenarios"
        $reports = @(Get-ChildItem "$name/.wini/tests/*/results.json")
        Assert ($reports.Count -eq 1) "$template writes test evidence"
        $scenarios = Get-Content $reports[0].FullName -Raw | ConvertFrom-Json
        $expectedScenarios = if ($template -eq 'network') { 3 } else { 2 }
        Assert ($scenarios.Count -eq $expectedScenarios -and @($scenarios | Where-Object { !$_.passed }).Count -eq 0) "$template functional and cancelled lifecycle scenarios pass"
        if ($template -eq 'network') {
            Assert ($scenarios[1].deniedNetworkRequests -eq 1 -and $scenarios[1].diagnostics -eq 1 -and $scenarios[1].notifications -eq 0) 'Network template handles a denied request without publishing success'
        }
        & $cli pack $name
        Assert ($LASTEXITCODE -eq 0) "Pack $template"
        $zip = @(Get-ChildItem "$name/dist/*.zip")
        Assert ($zip.Count -eq 1) "$template emits one release ZIP"
        Assert ((Get-FileHash $zip[0].FullName -Algorithm SHA256).Hash -eq (Get-Content ($zip[0].FullName + '.sha256') -Raw).Trim()) "$template ZIP matches digest"
        $expanded = Join-Path $OutputRoot "expanded-$template"
        Expand-Archive $zip[0].FullName $expanded
        Assert (!(Test-Path "$expanded/WindowsIsland.Contracts.dll")) "$template uses host Contracts"
        & $cli doctor $expanded
        Assert ($LASTEXITCODE -eq 0) "Inspect $template package"
    }
} finally { Pop-Location }
$releaseSamples = Join-Path $OutputRoot 'release-samples'
Copy-Item -LiteralPath "$repository/samples" -Destination $releaseSamples -Recurse
foreach ($sample in Get-ChildItem $releaseSamples -Directory) {
    $sourceProject = @(Get-ChildItem "$($sample.FullName)/*.csproj")
    Assert ($sourceProject.Count -eq 1) "$($sample.Name) release sample project exists"
    $definition = [xml](Get-Content $sourceProject[0].FullName -Raw)
    foreach ($reference in $definition.SelectNodes('/Project/ItemGroup/PackageReference[@Include="WindowsIsland.Contracts"]')) {
        $reference.SetAttribute('Version', $Version)
    }
    $definition.Save($sourceProject[0].FullName)
    $target = Join-Path $projects $sample.Name
    Copy-Item -LiteralPath $sample.FullName -Destination $target -Recurse
    $project = @(Get-ChildItem "$target/*.csproj")
    Assert ($project.Count -eq 1) "$($sample.Name) standalone sample project exists"
    dotnet restore $project[0].FullName --source $feed
    Assert ($LASTEXITCODE -eq 0) "$($sample.Name) restores standalone Contracts package"
    & $cli test $target
    Assert ($LASTEXITCODE -eq 0) "$($sample.Name) independent sample functional checks"
    & $cli pack $target
    Assert ($LASTEXITCODE -eq 0) "$($sample.Name) independent sample package"
}
Compress-Archive -Path "$releaseSamples/*" -DestinationPath "$feed/WindowsIsland.Plugin.Samples.$Version.zip"
Get-ChildItem $feed -File | Where-Object { $_.Extension -in '.nupkg', '.zip' } | ForEach-Object {
    $digest = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($_.FullName + '.sha256'), "$digest  $($_.Name)`n", [Text.UTF8Encoding]::new($false))
}
$checks | ConvertTo-Json | Set-Content (Join-Path $OutputRoot 'checks.json') -Encoding UTF8
Write-Output "$($checks.Count) SDK checks passed."
$global:LASTEXITCODE = 0
