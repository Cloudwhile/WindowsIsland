param(
    [Parameter(Mandatory)][string]$InstallDirectory,
    [int]$ParentId = 0,
    [long]$ParentStartedUtcTicks = 0
)
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if ($ParentId -gt 0) {
    $parent = Get-Process -Id $ParentId -ErrorAction SilentlyContinue
    if ($parent) {
        try {
            if ($parent.StartTime.ToUniversalTime().Ticks -ne $ParentStartedUtcTicks -or
                $parent.Path -ine (Join-Path $directory 'WindowsIsland.exe')) { throw 'Parent identity mismatch.' }
            if (!$parent.WaitForExit(30000)) { throw 'Application did not exit before unregistration.' }
        } finally { $parent.Dispose() }
    }
}
[xml]$manifest = Get-Content -LiteralPath (Join-Path $directory 'Initialization/AppxManifest.xml') -Raw -Encoding UTF8
$name = [string]$manifest.Package.Identity.Name
if (!$name) { throw 'Missing package identity.' }
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Management.Deployment.PackageManager,Windows.Management.Deployment,ContentType=WindowsRuntime]
$null = [Windows.Management.Deployment.DeploymentResult,Windows.Management.Deployment,ContentType=WindowsRuntime]
$manager = New-Object Windows.Management.Deployment.PackageManager
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 2 -and $_.GetParameters().Count -eq 1
} | Select-Object -First 1
foreach ($package in @($manager.FindPackagesForUser(''))) {
    if ($package.Id.Name -cne $name -or $package.InstalledLocation.Path.TrimEnd('\') -ine $directory) { continue }
    $operation = $manager.RemovePackageAsync($package.Id.FullName, [Windows.Management.Deployment.RemovalOptions]::PreserveApplicationData)
    $task = $asTask.MakeGenericMethod([Windows.Management.Deployment.DeploymentResult], [Windows.Management.Deployment.DeploymentProgress]).Invoke($null, @($operation))
    $result = $task.GetAwaiter().GetResult()
    if ($result.ExtendedErrorCode) { throw $result.ExtendedErrorCode }
}
