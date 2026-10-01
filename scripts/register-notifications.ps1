[CmdletBinding()]
param(
    [switch]$PrepareOnly,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $workspace 'artifacts\publish'
$project = Join-Path $workspace 'src\WindowsIsland\WindowsIsland.csproj'

if (!$NoBuild) {
    & dotnet publish $project -c Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}
if (!(Test-Path -LiteralPath (Join-Path $publish 'WindowsIsland.exe'))) {
    throw 'Publish the application before using -NoBuild.'
}

Copy-Item -LiteralPath (Join-Path $workspace 'packaging\AppxManifest.xml') -Destination (Join-Path $publish 'AppxManifest.xml') -Force
Copy-Item -LiteralPath (Join-Path $publish 'WindowsIsland.pri') -Destination (Join-Path $publish 'resources.pri') -Force
# A self-contained packaged process needs the runtime's WinRT class registrations.
# The unpackaged registration-free activation path alone does not supply these.
$intermediate = & dotnet msbuild $project -p:Configuration=Release -p:Platform=x64 -getProperty:IntermediateOutputPath
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the runtime manifest location.' }
$runtimeManifest = Join-Path ($intermediate.Trim()) 'MsixContent\AppxManifest.xml'
if (!(Test-Path -LiteralPath $runtimeManifest)) { throw "Missing runtime manifest: $runtimeManifest" }
$manifestPath = Join-Path $publish 'AppxManifest.xml'
$manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
$runtime = [xml](Get-Content -LiteralPath $runtimeManifest -Raw)
$extensions = $manifest.CreateElement('Extensions', $manifest.DocumentElement.NamespaceURI)
foreach ($extension in $runtime.Package.Extensions.Extension) {
    if ($extension.Category -in @('windows.activatableClass.inProcessServer', 'windows.activatableClass.proxyStub')) {
        if ($extension.InProcessServer -and !(Test-Path -LiteralPath (Join-Path $publish $extension.InProcessServer.Path))) { continue }
        [void]$extensions.AppendChild($manifest.ImportNode($extension, $true))
    }
}
[void]$manifest.DocumentElement.InsertBefore($extensions, $manifest.Package.Capabilities)
$manifest.Save($manifestPath)
$assets = Join-Path $publish 'Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
Add-Type -AssemblyName System.Drawing
foreach ($logo in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
    $size = [int]$logo[1]
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $ink = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(115, 239, 187))
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::FromArgb(7, 8, 10))
        $left = [single]($size * 0.12); $top = [single]($size * 0.35)
        $height = [single]($size * 0.3); $width = [single]($size * 0.76)
        $path.AddArc($left, $top, $height, $height, 90, 180)
        $path.AddArc(($left + $width - $height), $top, $height, $height, 270, 180)
        $path.CloseFigure()
        $graphics.FillPath($ink, $path)
        $bitmap.Save((Join-Path $assets $logo[0]), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $ink.Dispose(); $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}

if ($PrepareOnly) {
    Write-Output "Prepared notification-enabled app: $publish"
    return
}

# Development registration grants package identity without creating or trusting a certificate.
# Windows developer mode must already be enabled. Do not alter that system setting here.
$existing = Get-AppxPackage -Name WindowsIsland.Desktop
if ($existing -and ([version]$existing.Version -ge [version]$manifest.Package.Identity.Version)) {
    $version = [version]$existing.Version
    if ($version.Revision -ge 65535) { throw 'Package revision exhausted. Bump the manifest version.' }
    $manifest.Package.Identity.Version = '{0}.{1}.{2}.{3}' -f $version.Major, $version.Minor, $version.Build, ($version.Revision + 1)
    $manifest.Save($manifestPath)
}
Add-AppxPackage -Register (Join-Path $publish 'AppxManifest.xml')
$package = Get-AppxPackage -Name WindowsIsland.Desktop
if (!$package) { throw 'Package registration did not complete.' }
Write-Output 'Registered Windows Island. Open it from Start and allow notification access.'
Write-Output "Application ID: $($package.PackageFamilyName)!App"
