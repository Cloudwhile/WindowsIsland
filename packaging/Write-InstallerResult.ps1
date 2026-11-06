param([Parameter(Mandatory)][string]$InstallDirectory, [Parameter(Mandatory)][string]$Version, [switch]$Failed)
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$success = !$Failed
foreach ($name in @('WindowsIsland.exe','WindowsIsland.dll')) {
    $file = Join-Path $directory $name
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { $success = $false; continue }
    $product = [Diagnostics.FileVersionInfo]::GetVersionInfo($file).ProductVersion
    if (!$product -or $product.Split('+')[0].TrimStart('v') -cne $Version.TrimStart('v')) { $success = $false }
}
$root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WindowsIsland/Updates'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$result = [ordered]@{ InstallDirectory=$directory; Version=$Version; Success=$success; Message=''; MessageKey=$(if ($success) { 'UpdateComplete' } else { 'UpdateFailed' }) }
$destination = Join-Path $root 'result.json'
$temporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
[IO.File]::WriteAllText($temporary, (ConvertTo-Json $result), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $destination -Force
