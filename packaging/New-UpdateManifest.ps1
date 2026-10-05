#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ReleaseDataPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$RequiredTag
)

$ErrorActionPreference = 'Stop'
$repository = 'Cloudwhile/WindowsIsland'
$downloadPrefix = "https://github.com/$repository/releases/download/"
$maxManifestBytes = 4 * 1024 * 1024

function Test-ReleaseTag([string]$tag) {
    $match = [regex]::Match($tag, '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')
    if (!$match.Success) { return $false }
    $versionPart = 0
    foreach ($index in 1..3) {
        if (![int]::TryParse($match.Groups[$index].Value, [ref]$versionPart)) { return $false }
    }
    foreach ($part in $match.Groups[4].Value.Split('.')) {
        if ($part -cmatch '^[0-9]+$' -and $part.Length -gt 1 -and $part[0] -eq '0') { return $false }
    }
    return $true
}

function Convert-Release($release) {
    if ($release -isnot [Collections.IDictionary] -or $release.draft -isnot [bool] -or $release.draft -or
        $release.prerelease -isnot [bool] -or $release.tag_name -isnot [string] -or
        !(Test-ReleaseTag $release.tag_name) -or $release.assets -isnot [array]) { return }
    $tag = $release.tag_name
    $baseName = "WindowsIsland-$($tag.Substring(1))-win-x64"
    $assets = [Collections.Generic.List[object]]::new()
    foreach ($extension in '.zip', '.zip.sha256', '.msi', '.msi.sha256') {
        $name = "$baseName$extension"
        $matching = @($release.assets | Where-Object { $_ -is [Collections.IDictionary] -and $_.name -ceq $name })
        if ($matching.Count -ne 1) { return }
        $asset = $matching[0]
        $url = $downloadPrefix + [Uri]::EscapeDataString($tag) + '/' + [Uri]::EscapeDataString($name)
        if ($asset.browser_download_url -cne $url -or ($asset.size -isnot [long] -and $asset.size -isnot [int]) -or
            $asset.size -le 0 -or ($asset.Contains('state') -and $asset.state -cne 'uploaded')) { return }
        $assets.Add([ordered]@{ name = $name; browser_download_url = $url; size = $asset.size })
    }
    $title = if ($release.name -is [string] -and $release.name.Length) { $release.name } else { $tag }
    $notes = if ($release.body -is [string]) { $release.body } else { '' }
    [ordered]@{
        tag_name = $tag
        name = $title
        body = $notes.Replace("`r`n", "`n")
        prerelease = $release.prerelease -or $tag.Split('+')[0].Contains('-')
        assets = $assets.ToArray()
    }
}

# gh api --paginate --slurp produces an outer array of release pages.
$pages = Get-Content -LiteralPath $ReleaseDataPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -NoEnumerate
if ($pages -isnot [array]) { throw 'Release metadata must be an array.' }
$entries = foreach ($page in $pages) {
    foreach ($release in $page) { Convert-Release $release }
}
$entries = @($entries | Sort-Object -Property { $_.tag_name } -CaseSensitive)
if (@($entries | Group-Object -Property { $_.tag_name } -CaseSensitive | Where-Object Count -gt 1).Count) {
    throw 'Release metadata contains duplicate tags.'
}
if ($RequiredTag -and $RequiredTag -cnotin $entries.tag_name) {
    throw "The published release is missing or incomplete: $RequiredTag"
}
$manifest = [ordered]@{ schema_version = 1; releases = $entries } | ConvertTo-Json -Depth 10
$bytes = [Text.UTF8Encoding]::new($false).GetBytes($manifest + "`n")
if ($bytes.Length -gt $maxManifestBytes) { throw 'The update manifest exceeds the client size limit.' }
$output = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
[IO.File]::WriteAllBytes($output, $bytes)
