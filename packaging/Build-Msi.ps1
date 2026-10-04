param(
    [Parameter(Mandatory)][string]$Version,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/distribution'),
    [string]$Wix = (Join-Path $PSScriptRoot '../artifacts/tools/wix/wix.exe')
)
$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$work = Join-Path $PSScriptRoot '../artifacts/msi'
New-Item -ItemType Directory -Path $output,$work -Force | Out-Null
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$') { throw 'Invalid package version.' }
$parts = @([int]$Matches[1],[int]$Matches[2],[int]$Matches[3])
if ($parts[0] -gt 255 -or $parts[1] -gt 255 -or $parts[2] -gt 65535) { throw 'Version exceeds MSI limits.' }
$msiVersion = $parts -join '.'
if (!(Test-Path -LiteralPath (Join-Path $publish 'WindowsIsland.exe'))) { throw 'Publish output is missing.' }
function Escape-Xml([string]$value) { [Security.SecurityElement]::Escape($value) }
function Get-Id([string]$value) {
    $bytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant()))
    'id' + [Convert]::ToHexString($bytes).Substring(0,24)
}
$xml = [Text.StringBuilder]::new()
[void]$xml.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Fragment>')
$directories = @{ '' = 'INSTALLFOLDER' }
foreach ($directory in @(Get-ChildItem -LiteralPath $publish -Directory -Recurse | Sort-Object FullName)) {
    $relative = [IO.Path]::GetRelativePath($publish,$directory.FullName)
    $parent = [IO.Path]::GetDirectoryName($relative)
    $id = Get-Id "directory/$relative"
    $directories[$relative] = $id
    [void]$xml.AppendLine(('<DirectoryRef Id="{0}"><Directory Id="{1}" Name="{2}" /></DirectoryRef>' -f $directories[$parent],$id,(Escape-Xml $directory.Name)))
}
[void]$xml.AppendLine('<ComponentGroup Id="ApplicationFiles">')
foreach ($file in @(Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName)) {
    $relative = [IO.Path]::GetRelativePath($publish,$file.FullName)
    $directory = $directories[[IO.Path]::GetDirectoryName($relative)]
    $id = Get-Id "file/$relative"
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("WindowsIsland/x64/perUser/$relative".ToLowerInvariant()))
    $guid = [Guid]::new([byte[]]$hash[0..15]).ToString()
    [void]$xml.AppendLine(('<Component Id="{0}" Directory="{1}" Guid="{2}">' -f $id,$directory,$guid))
    [void]$xml.AppendLine(('<File Id="file_{0}" Source="{1}" />' -f $id,(Escape-Xml $file.FullName)))
    [void]$xml.AppendLine(('<RegistryValue Root="HKCU" Key="Software\Cloudwhile\WindowsIsland\Installer\Files" Name="{0}" Value="1" Type="integer" KeyPath="yes" />' -f $id))
    if ($directory -ne 'INSTALLFOLDER') { [void]$xml.AppendLine(('<RemoveFolder Id="remove_{0}" On="uninstall" />' -f $id)) }
    [void]$xml.AppendLine('</Component>')
}
[void]$xml.AppendLine('</ComponentGroup></Fragment></Wix>')
$payload = Join-Path $work 'Payload.wxs'
[IO.File]::WriteAllText($payload,$xml.ToString(),[Text.UTF8Encoding]::new($false))
$msi = Join-Path $output "WindowsIsland-$Version-win-x64.msi"
& $Wix build (Join-Path $PSScriptRoot 'WindowsIsland.wxs') $payload -arch x64 -d "ReleaseVersion=$Version" -d "MsiVersion=$msiVersion" -d "PublishDirectory=$publish" -d "PackagingDirectory=$PSScriptRoot" -intermediateFolder $work -pdbtype none -o $msi
if ($LASTEXITCODE -ne 0) { throw "WiX build failed: $LASTEXITCODE" }
$digest = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$msi.sha256", "$digest  $([IO.Path]::GetFileName($msi))`n", [Text.UTF8Encoding]::new($false))
Write-Output $msi
