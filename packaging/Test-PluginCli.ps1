param([Parameter(Mandatory=$true)][string]$OutputRoot, [string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw 'Use a new isolated output directory.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$repository = Split-Path $PSScriptRoot -Parent
$feed = Join-Path $OutputRoot 'feed'
$toolPath = Join-Path $OutputRoot 'tool'
$samples = Join-Path $OutputRoot 'samples'
$checks = [Collections.Generic.List[string]]::new()
function Assert($condition, [string]$name) {
    if (!$condition) { throw "FAIL: $name" }
    $checks.Add($name)
    Write-Output "PASS: $name"
}
dotnet pack "$repository/src/WindowsIsland.Plugin.Cli/WindowsIsland.Plugin.Cli.csproj" -c Release "-p:Version=$Version" -o $feed --disable-build-servers
Assert ($LASTEXITCODE -eq 0) 'CLI tool package builds'
# 只使用本轮 feed 安装，不使用全局工具目录或 NuGet 公共源。
$config = Join-Path $OutputRoot 'NuGet.Config'
'<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content $config -Encoding UTF8
dotnet tool install WindowsIsland.Plugin.Cli --version $Version --tool-path $toolPath --add-source $feed --configfile $config
Assert ($LASTEXITCODE -eq 0) 'Install standalone CLI from local feed'
$cli = Join-Path $toolPath 'wini-plugin.exe'
function Invoke-Cli {
    # Windows PowerShell 5.1 将预期的原生 stderr 转成 ErrorRecord；以退出码作断言。
    $ErrorActionPreference = 'Continue'
    & $cli @args 2>&1 | ForEach-Object { Write-Output $_.ToString() }
    $script:cliResult = $LASTEXITCODE
}
New-Item -ItemType Directory -Path $samples | Out-Null
Push-Location $samples
try {
    foreach ($template in @('notification','pinned','network')) {
        $name = 'Example' + $template
        Invoke-Cli init $name --template $template
        Assert ($cliResult -eq 0) "Generate $template from empty directory"
        Invoke-Cli validate $name
        Assert ($cliResult -eq 0) "Validate $template source"
        Invoke-Cli pack $name
        Assert ($cliResult -eq 0) "Build and pack $template outside repository"
        $zip = @(Get-ChildItem "$name/dist/*.zip")
        Assert ($zip.Count -eq 1) "$template produces exactly one ZIP"
        Assert ((Get-FileHash $zip[0].FullName -Algorithm SHA256).Hash -eq (Get-Content ($zip[0].FullName + '.sha256') -Raw).Trim()) "$template digest matches ZIP"
        $expanded = Join-Path $OutputRoot "expanded-$template"
        Expand-Archive $zip[0].FullName $expanded
        Assert (!(Test-Path "$expanded/WindowsIsland.Contracts.dll")) "$template does not bundle host contract"
        Invoke-Cli doctor $expanded
        Assert ($cliResult -eq 0) "$template package passes host metadata and dependency checks"
        # 已有目录及发布 ZIP 不得被覆盖。
        Invoke-Cli init $name --template $template
        Assert ($cliResult -ne 0) "$template existing project is preserved"
    }
    $manifestPath = Join-Path $samples 'Examplenotification/manifest.json'
    $original = [IO.File]::ReadAllText($manifestPath)
    $manifest = $original | ConvertFrom-Json
    $manifest.permissions = @('unknown')
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10))
    Invoke-Cli validate Examplenotification
    Assert ($cliResult -eq 0) 'Developer-defined permission labels pass validation'
    $manifest.permissions = @('notifications', 'notifications')
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10))
    Invoke-Cli validate Examplenotification
    Assert ($cliResult -ne 0) 'Duplicate permission labels fail validation'
    [IO.File]::WriteAllText($manifestPath, $original)
    $entry = Join-Path $OutputRoot 'expanded-notification/Examplenotification.dll'
    $packageRoot = Join-Path $OutputRoot 'expanded-notification'
    $packageManifest = Join-Path $packageRoot 'manifest.json'
    $packageOriginal = [IO.File]::ReadAllText($packageManifest)
    $minimum = $packageOriginal | ConvertFrom-Json
    $minimum | Add-Member -NotePropertyName minimumHostVersion -NotePropertyValue '1.2.3' -Force
    [IO.File]::WriteAllText($packageManifest, ($minimum | ConvertTo-Json -Depth 10))
    foreach ($target in @('1.2.3','v1.2.3','1.2.4','2.0.0')) {
        Invoke-Cli doctor $packageRoot --host-version $target
        Assert ($cliResult -eq 0) "Doctor accepts compatible target $target"
    }
    foreach ($target in @('1.2.2','1.2.3-alpha','invalid','vv1.2.3')) {
        Invoke-Cli doctor $packageRoot --host-version $target
        Assert ($cliResult -ne 0) "Doctor rejects incompatible or malformed target $target"
    }
    $minimum.minimumHostVersion = '1.02.3'
    [IO.File]::WriteAllText($packageManifest, ($minimum | ConvertTo-Json -Depth 10))
    Invoke-Cli doctor $packageRoot --host-version '2.0.0'
    Assert ($cliResult -ne 0) 'Doctor rejects malformed minimum host declaration'
    [IO.File]::WriteAllText($packageManifest, $packageOriginal)
    Move-Item -LiteralPath $entry -Destination ($entry + '.saved')
    Invoke-Cli doctor (Join-Path $OutputRoot 'expanded-notification')
    Assert ($cliResult -ne 0) 'Missing entry fails metadata validation'
    Move-Item -LiteralPath ($entry + '.saved') -Destination $entry
    Invoke-Cli init '../escape'
    Assert ($cliResult -ne 0) 'Traversal name fails generation'
    $before = (Get-FileHash 'Examplenotification/dist/*.zip').Hash
    Invoke-Cli pack Examplenotification
    Assert ($cliResult -ne 0) 'Packing refuses an existing release ZIP'
    Assert ($before -eq (Get-FileHash 'Examplenotification/dist/*.zip').Hash) 'Existing release ZIP is unchanged'
} finally { Pop-Location }
& (Join-Path $PSScriptRoot 'Test-PluginCliRegression.ps1') -CliPath $cli -OutputRoot (Join-Path $OutputRoot 'regressions')
Assert ($LASTEXITCODE -eq 0) 'Illegal callbacks fail and isolated test profiles are reclaimed'
$checks | ConvertTo-Json | Set-Content (Join-Path $OutputRoot 'checks.json') -Encoding UTF8
Write-Output "$($checks.Count) CLI checks passed."
$global:LASTEXITCODE = 0
