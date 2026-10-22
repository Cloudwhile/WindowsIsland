#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/distribution'),
    [string]$InnoSetup = ''
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$') { throw 'Invalid package version.' }
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (!(Test-Path -LiteralPath (Join-Path $publish 'WindowsIsland.exe') -PathType Leaf)) { throw 'Publish output is missing.' }
if (!(Test-Path -LiteralPath (Join-Path $publish 'LICENSE') -PathType Leaf)) { throw 'Publish output is missing LICENSE.' }
if (!(Test-Path -LiteralPath (Join-Path $publish 'WindowsIsland.TelegramHook.dll') -PathType Leaf)) {
    throw 'Publish output is missing WindowsIsland.TelegramHook.dll.'
}
if (!$InnoSetup) {
    $InnoSetup = (Get-Command iscc.exe -ErrorAction SilentlyContinue)?.Source
    if (!$InnoSetup) {
        $candidate = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
        if (Test-Path -LiteralPath $candidate) { $InnoSetup = $candidate }
    }
}
if (!$InnoSetup -or !(Test-Path -LiteralPath $InnoSetup -PathType Leaf)) { throw 'Inno Setup compiler ISCC.exe was not found.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$arguments = @(
    "/DAppVersion=$Version",
    "/DPublishDirectory=$publish",
    "/DOutputDirectory=$output",
    "/DPackagingDirectory=$([IO.Path]::GetFullPath($PSScriptRoot))",
    (Join-Path $PSScriptRoot 'WindowsIsland.iss')
)
& $InnoSetup $arguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
$installer = Join-Path $output "WindowsIsland-$Version-win-x64.exe"
if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Inno Setup did not produce an installer.' }
$digest = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$installer.sha256", "$digest  $([IO.Path]::GetFileName($installer))`n", [Text.UTF8Encoding]::new($false))
Write-Output $installer

# ZIP is a distribution format only; both modes update through the same EXE.
$portable = Join-Path $output 'portable-staging'
if (Test-Path -LiteralPath $portable) { throw 'Portable staging already exists.' }
New-Item -ItemType Directory -Path $portable | Out-Null
try {
    foreach ($item in Get-ChildItem -LiteralPath $publish -Force) {
        if ($item.Name -like 'verification-*' -or $item.Name -eq 'Updater' -or $item.Name -like 'unins*' -or $item.Name -eq 'installer-policy.json') { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $portable -Recurse
    }
    [IO.File]::WriteAllText((Join-Path $portable 'portable.marker'), '', [Text.UTF8Encoding]::new($false))
    $archive = Join-Path $output "WindowsIsland-$Version-win-x64.zip"
    Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $archive -Force
    $digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$archive.sha256", "$digest  $([IO.Path]::GetFileName($archive))`n", [Text.UTF8Encoding]::new($false))
    Write-Output $archive
} finally {
    $resolved = [IO.Path]::GetFullPath($portable)
    if ([IO.Path]::GetDirectoryName($resolved) -ine $output -or [IO.Path]::GetFileName($resolved) -cne 'portable-staging') { throw 'Invalid portable staging path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
