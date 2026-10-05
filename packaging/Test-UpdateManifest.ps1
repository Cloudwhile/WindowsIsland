#Requires -Version 7.0
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/verification/update-manifest'))

$ErrorActionPreference = 'Stop'
$generator = Join-Path $PSScriptRoot 'New-UpdateManifest.ps1'
$root = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$source = Join-Path $root 'releases.json'
$output = Join-Path $root 'manifest.json'
$script:checks = 0

function Check([bool]$condition, [string]$message) {
    if (!$condition) { throw $message }
    $script:checks++
    Write-Host "PASS $message"
}

function Release([string]$tag, [bool]$prerelease = $false, [bool]$draft = $false) {
    $name = "WindowsIsland-$($tag.Substring(1))-win-x64"
    @{
        tag_name = $tag; name = "Windows Island $tag"; body = "修复与改进`r`n更新说明"
        prerelease = $prerelease; draft = $draft; author = @{ login = 'not-needed' }
        assets = @('.zip', '.zip.sha256', '.msi', '.msi.sha256' | ForEach-Object {
            @{ name = "$name$_"; size = 1024; state = 'uploaded'
               browser_download_url = "https://github.com/Cloudwhile/WindowsIsland/releases/download/$tag/$name$_" }
        })
    }
}

function Write-Metadata($value) {
    [IO.File]::WriteAllText($source, (ConvertTo-Json -InputObject $value -Depth 10), [Text.UTF8Encoding]::new($false))
}

function Reject($value, [string]$requiredTag, [string]$message) {
    Write-Metadata $value
    $before = [IO.File]::ReadAllBytes($output)
    $failed = $false
    try { & $generator -ReleaseDataPath $source -OutputPath $output -RequiredTag $requiredTag }
    catch { $failed = $true }
    Check ($failed -and [Convert]::ToBase64String($before) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($output))) $message
}

$stable = Release 'v1.4.0'
$preview = Release 'v1.5.0-rc.10'
$flaggedStable = Release 'v1.4.1' $true
$draft = Release 'v9.0.0' $false $true
$missing = Release 'v8.0.0'
$missing.assets = $missing.assets[0..2]
$foreign = Release 'v7.0.0'
$foreign.assets[0].browser_download_url = 'https://example.com/archive.zip'
$wrongVersion = Release 'v6.0.0'
$wrongVersion.assets[0].browser_download_url = $wrongVersion.assets[0].browser_download_url.Replace('/v6.0.0/', '/v5.0.0/')
$wrongSize = Release 'v5.0.0'
$wrongSize.assets[0].size = '1024'
$empty = Release 'v4.0.0'
$empty.assets[0].size = 0
$unfinished = Release 'v3.0.0'
$unfinished.assets[0].state = 'new'
$duplicate = Release 'v2.0.0'
$duplicate.assets += $duplicate.assets[0]
$badFlag = Release 'v1.9.0'
$badFlag.prerelease = 'false'
$invalidTags = @('v01.0.0', 'v1.0.0-rc.01', 'v2147483648.0.0', 'v1.0.0-rc..1') | ForEach-Object { Release $_ }
$pages = @(@($draft, $preview, $foreign, $wrongSize, $unfinished, $badFlag),
    @($stable, $missing, $flaggedStable, $wrongVersion, $empty, $duplicate) + $invalidTags)
Write-Metadata $pages
& $generator -ReleaseDataPath $source -OutputPath $output -RequiredTag 'v1.5.0-rc.10'
$manifest = Get-Content -LiteralPath $output -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
Check ($manifest.schema_version -eq 1 -and $manifest.releases.Count -eq 3) 'All pages are scanned and drafts, incomplete and invalid releases are omitted'
Check ('v1.4.0' -cin $manifest.releases.tag_name) 'Publishing a preview retains the previous stable version'
Check (@($manifest.releases | Where-Object { $_.prerelease }).Count -eq 2) 'Both release flags and prerelease tag suffixes select the preview channel'
Check (@($manifest.releases.assets).Count -eq 12 -and @($manifest.releases.assets | Where-Object { $_.size -le 0 }).Count -eq 0) 'Every published entry has the four correctly sized ZIP and MSI assets'
Check ($manifest.releases[0].body -ceq "修复与改进`n更新说明" -and !$manifest.releases[0].Contains('author')) 'Notes retain Chinese text and omit unrelated metadata'
$bytes = [IO.File]::ReadAllBytes($output)
Check (!($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) -and
    ![IO.File]::ReadAllText($output).Contains('\u')) 'The manifest uses UTF-8 without BOM or escaped Chinese'
$expected = [Convert]::ToBase64String($bytes)
Write-Metadata @($stable, $flaggedStable, $preview)
& $generator -ReleaseDataPath $source -OutputPath $output -RequiredTag 'v1.4.0'
Check ($expected -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($output))) 'Equivalent flat metadata produces identical bytes regardless of input order'
Reject @(@($stable, $preview)) 'v9.9.9' 'A missing current tag fails without overwriting the feed'
Reject @($stable, $missing) 'v8.0.0' 'An incomplete current release fails without overwriting the feed'
Reject @($stable, $stable) 'v1.4.0' 'Duplicate tags fail without overwriting the feed'
Reject @{ releases = @($stable) } 'v1.4.0' 'An invalid root fails without overwriting the feed'
$oversized = Release 'v1.4.0'
$oversized.body = 'x' * (4 * 1024 * 1024)
Reject @($oversized) 'v1.4.0' 'An oversized generated feed fails before replacing the existing file'
Write-Metadata @()
& $generator -ReleaseDataPath $source -OutputPath $output
$manifest = Get-Content -LiteralPath $output -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
Check ($manifest.releases -is [array] -and $manifest.releases.Count -eq 0) 'An empty release collection preserves the manifest schema'
Write-Host "Passed $script:checks update manifest checks."
