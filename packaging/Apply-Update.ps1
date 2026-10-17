param([Parameter(Mandatory)][string]$JobPath, [switch]$ElevatedWorker, [int]$BootstrapId = 0)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Full-Path([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { throw '更新路径为空。' }
    [IO.Path]::GetFullPath($path).TrimEnd('\', '/')
}

function Assert-NoLinks([string]$path) {
    $current = $path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '更新路径包含链接。' }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Child-Path([string]$root, [string]$relative) {
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or
        @($relative.Replace('\', '/').Split('/') | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_ -eq '' }).Count -gt 0) {
        throw '更新文件路径无效。'
    }
    $path = Full-Path (Join-Path $root $relative)
    if (!$path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '更新文件超出目录。' }
    Assert-NoLinks $path
    $path
}

function Test-InstallerFileName([string]$name) {
    $name -match '^WindowsIsland-(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?-win-x64\.msi$'
}

function Copy-Retry([string]$source, [string]$destination) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    for ($attempt = 0; ; $attempt++) {
        try { [IO.File]::Copy($source, $destination, $true); return }
        catch [IO.IOException] {
            if ($attempt -ge 19) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
}

function File-Digest([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($algorithm.ComputeHash($stream)) }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Assert-InstalledVersion {
    $expected = ([string]$job.Version -replace '^v', '').Split('+')[0]
    foreach ($name in @('WindowsIsland.exe', 'WindowsIsland.dll')) {
        $path = Join-Path $install $name
        $actual = ([Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion -replace '^v', '').Split('+')[0]
        if (!$actual -or $actual -cne $expected) { throw '更新后的程序版本与目标版本不一致。' }
    }
}

function Write-UpdateError([object]$failure) {
    $detail = $stage + [Environment]::NewLine + ($failure | Out-String)
    if ($detail.Length -gt 8192) { $detail = $detail.Substring(0, 8192) }
    try { [IO.File]::WriteAllText((Join-Path $jobDirectory 'error.log'), $detail, [Text.UTF8Encoding]::new($true)) }
    catch { }
}

$stage = 'Task'
$jobDirectory = $null
try {
    $jobDirectory = Full-Path ([IO.Path]::GetDirectoryName((Full-Path $JobPath)))
    $workRoot = [IO.Path]::GetDirectoryName($jobDirectory)
    $jobId = [Guid]::Empty
    if ((!$ElevatedWorker -and !(Full-Path $PSScriptRoot).Equals($jobDirectory, [StringComparison]::OrdinalIgnoreCase)) -or
        ![Guid]::TryParseExact([IO.Path]::GetFileName($jobDirectory), 'N', [ref]$jobId)) {
        throw '更新任务目录无效。'
    }
    Assert-NoLinks $jobDirectory
    $stage = 'Paths'
    $job = Get-Content -LiteralPath $JobPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$job.PSObject.Properties['RequiresElevation'] -or $job.RequiresElevation -isnot [bool] -or
        !$job.PSObject.Properties['UserSid'] -or [string]$job.UserSid -notmatch '^S-1-(?:\d+-){1,14}\d+$') {
        throw '更新权限信息无效。'
    }
    if ($ElevatedWorker) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try {
            if (!([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                throw '更新辅助程序没有管理员权限。'
            }
        } finally { $identity.Dispose() }
        if (!$job.RequiresElevation -or $BootstrapId -le 0) { throw '更新任务不需要管理员权限。' }
    }
    $install = Full-Path $job.InstallDirectory
    $payload = Full-Path $job.PayloadDirectory
    $backup = Join-Path $jobDirectory 'backup'
    $executable = Join-Path $install 'WindowsIsland.exe'
    if ($install -eq [IO.Path]::GetPathRoot($install).TrimEnd('\') -or
        $install.StartsWith($workRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        !$payload.Equals((Join-Path $jobDirectory 'payload'), [StringComparison]::OrdinalIgnoreCase)) {
        throw '更新目标目录无效。'
    }
    Assert-NoLinks $install
    Assert-NoLinks $payload
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw '消息岛程序不存在。' }
    $stage = 'Parent'
    $parent = [Diagnostics.Process]::GetProcessById([int]$job.ProcessId)
    if ($parent.StartTime.ToUniversalTime().Ticks -ne [long]$job.ProcessStartedUtcTicks -or
        !(Full-Path $parent.MainModule.FileName).Equals($executable, [StringComparison]::OrdinalIgnoreCase)) {
        throw '更新进程与安装目录不匹配。'
    }
    if ($ElevatedWorker) {
        try {
            $bootstrap = [Diagnostics.Process]::GetProcessById($BootstrapId)
            try {
                if (!(Full-Path $bootstrap.MainModule.FileName).Equals($executable, [StringComparison]::OrdinalIgnoreCase) -or
                    !$bootstrap.WaitForExit(10000)) { throw '更新权限辅助程序未退出。' }
            } finally { $bootstrap.Dispose() }
        } catch [ArgumentException] { }
    }
} catch {
    if ($stage -ne 'Task') { Write-UpdateError $_ }
    throw
}
$plan = [Collections.Generic.List[object]]::new()
$modified = $false
$canRestart = $false
$success = $false
$mutex = $null
$acquired = $false
$delegated = $false
$message = '更新未完成，请重试。'
$messageKey = 'UpdateFailed'
try {
    if ($job.InstallerPath) {
        $stage = 'Installer'
        $installer = Full-Path $job.InstallerPath
        if (!$job.PSObject.Properties['InstallerSourcePath'] -or
            [string]::IsNullOrWhiteSpace([string]$job.InstallerSourcePath)) {
            throw 'Installer source path is missing.'
        }
        $installerSource = Full-Path $job.InstallerSourcePath
        $installerName = [IO.Path]::GetFileName($installer)
        $sourceName = [IO.Path]::GetFileName($installerSource)
        if (![IO.Path]::GetDirectoryName($installer).Equals($jobDirectory, [StringComparison]::OrdinalIgnoreCase) -or
            ![IO.Path]::GetDirectoryName($installerSource).Equals($workRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $installerName -cne $sourceName -or !(Test-InstallerFileName $installerName) -or
            !(Test-Path -LiteralPath $installer -PathType Leaf) -or
            !(Test-Path -LiteralPath $installerSource -PathType Leaf)) { throw 'Installer path is invalid.' }
        Assert-NoLinks $installer
        Assert-NoLinks $installerSource
        $registrationPath = 'Registry::HKEY_USERS\' + [string]$job.UserSid + '\Software\Cloudwhile\WindowsIsland\Installer'
        $registration = Get-ItemProperty -LiteralPath $registrationPath
        if (!(Full-Path $registration.InstallDirectory).Equals($install, [StringComparison]::OrdinalIgnoreCase)) {
            throw '安装记录与更新目录不匹配。'
        }
    } else {
        $stage = 'Payload'
        if ($ElevatedWorker -or !$job.RequiresElevation) {
            $probe = Join-Path $install ('.island-update-' + $jobId.ToString('N'))
            [IO.File]::WriteAllText($probe, '')
            Remove-Item -LiteralPath $probe -Force
        }
        $files = @(Get-ChildItem -LiteralPath $payload -Recurse -File -Force)
        if ($files.Count -eq 0 -or !(Test-Path -LiteralPath (Join-Path $payload 'WindowsIsland.exe') -PathType Leaf)) {
            throw '更新包不完整。'
        }
        $owned = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($payload.Length + 1)
            [void](Child-Path $payload $relative)
            if ($relative -in @('AppxManifest.xml', 'resources.pri', 'Uninstall.ps1', 'settings.json', '.update-files.json')) { continue }
            [void]$owned.Add($relative)
            $target = Child-Path $install $relative
            if (Test-Path -LiteralPath $target -PathType Container) { throw '更新文件与已有目录冲突。' }
            $plan.Add([pscustomobject]@{ Relative = $relative; Target = $target; Source = $file.FullName; Existed = [IO.File]::Exists($target) })
        }
        $inventory = Join-Path $install '.update-files.json'
        if ([IO.File]::Exists($inventory)) {
            $previousFiles = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $inventory -Raw -Encoding UTF8)
            foreach ($relative in $previousFiles) {
                if ($relative -in @('AppxManifest.xml', 'resources.pri', 'Uninstall.ps1', 'settings.json', '.update-files.json')) { continue }
                $target = Child-Path $install $relative
                if (!$owned.Contains($relative) -and [IO.File]::Exists($target)) {
                    $plan.Add([pscustomobject]@{ Relative = $relative; Target = $target; Source = $null; Existed = $true })
                }
            }
        }
        if ([IO.File]::Exists((Join-Path $install 'resources.pri'))) {
            $plan.Add([pscustomobject]@{ Relative = 'resources.pri'; Target = (Child-Path $install 'resources.pri');
                Source = (Child-Path $payload 'WindowsIsland.pri'); Existed = $true })
        }
        $plan.Add([pscustomobject]@{ Relative = '.update-files.json'; Target = (Child-Path $install '.update-files.json');
            Source = $null; Existed = [IO.File]::Exists($inventory) })
    }
    if ($ElevatedWorker) {
        $stage = 'ElevatedReady'
        [IO.File]::WriteAllText((Join-Path $jobDirectory 'elevated-ready'), '')
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while (!(Test-Path -LiteralPath (Join-Path $jobDirectory 'elevated-commit'))) {
            if ([DateTime]::UtcNow -gt $deadline) { throw '更新权限交接超时。' }
            Start-Sleep -Milliseconds 100
        }
    } else {
        if ($job.RequiresElevation) {
            $stage = 'Elevation'
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            while (!(Test-Path -LiteralPath (Join-Path $jobDirectory 'elevated-ready'))) {
                if (Test-Path -LiteralPath (Join-Path $jobDirectory 'elevation-failed')) { throw '管理员权限更新辅助程序启动失败。' }
                if ([DateTime]::UtcNow -gt $deadline) { throw '管理员权限请求超时。' }
                Start-Sleep -Milliseconds 100
            }
        }
        $stage = 'Commit'
        [IO.File]::WriteAllText((Join-Path $jobDirectory 'ready'), '')
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while (!(Test-Path -LiteralPath (Join-Path $jobDirectory 'commit'))) {
            if ([DateTime]::UtcNow -gt $deadline) { throw '更新交接超时。' }
            Start-Sleep -Milliseconds 100
        }
        $stage = 'Exit'
        if (!$parent.WaitForExit(30000)) { throw '消息岛未退出，更新已取消。' }
        $canRestart = $true
        $stage = 'Mutex'
        $mutex = [Threading.Mutex]::new($false, [string]$job.MutexName)
        try { $acquired = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (!$acquired) { $canRestart = $false; throw '消息岛已经重新运行，更新已取消。' }
        if ($job.RequiresElevation) {
            $stage = 'ElevatedInstall'
            [IO.File]::WriteAllText((Join-Path $jobDirectory 'elevated-commit'), '')
            $deadline = [DateTime]::UtcNow.AddMinutes(15)
            while (!(Test-Path -LiteralPath (Join-Path $jobDirectory 'elevated-finished'))) {
                if ([DateTime]::UtcNow -gt $deadline) { throw '管理员权限更新安装超时。' }
                Start-Sleep -Milliseconds 100
            }
            $delegatedPath = Join-Path $workRoot 'result.json'
            if (!(Test-Path -LiteralPath $delegatedPath -PathType Leaf)) { throw '管理员权限更新辅助程序没有返回结果。' }
            $delegatedResult = Get-Content -LiteralPath $delegatedPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if (!(Full-Path $delegatedResult.InstallDirectory).Equals($install, [StringComparison]::OrdinalIgnoreCase) -or
                [string]$delegatedResult.Version -cne [string]$job.Version) { throw '管理员权限更新结果无效。' }
            $success = $delegatedResult.Success -eq $true
            $message = [string]$delegatedResult.Message
            $messageKey = [string]$delegatedResult.MessageKey
            $delegated = $true
        }
    }

    if (!$delegated) {
      $stage = 'Install'
      if ($job.InstallerPath) {
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = Join-Path ([Environment]::GetFolderPath('System')) 'msiexec.exe'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $log = Join-Path $jobDirectory 'msi.log'
        $start.Arguments = "/i `"$installerSource`" /qn /norestart INSTALLFOLDER=`"$install`" /L*v `"$log`""
        $installation = [Diagnostics.Process]::Start($start)
        try {
            $installation.WaitForExit()
            if ($installation.ExitCode -notin @(0, 3010)) { throw "安装更新失败：$($installation.ExitCode)" }
        } finally { $installation.Dispose() }
      } else {
        [IO.Directory]::CreateDirectory($backup) | Out-Null
        foreach ($item in $plan) {
            if ($item.Existed) { Copy-Retry $item.Target (Child-Path $backup $item.Relative) }
        }
        $modified = $true
        foreach ($item in $plan) {
            if ($item.Relative -eq '.update-files.json') { continue }
            if ($item.Source) { Copy-Retry $item.Source $item.Target }
            elseif ([IO.File]::Exists($item.Target)) { Remove-Item -LiteralPath $item.Target -Force }
        }
        $json = ConvertTo-Json -InputObject @($owned) -Compress
        [IO.File]::WriteAllText($inventory, $json, [Text.UTF8Encoding]::new($false))
      }
      $stage = 'Version'
      Assert-InstalledVersion
      $success = $true
      $message = '更新完成。'
      $messageKey = 'UpdateComplete'
    }
} catch {
    Write-UpdateError $_
    if ($modified) {
        try {
            for ($index = $plan.Count - 1; $index -ge 0; $index--) {
                $item = $plan[$index]
                if ($item.Existed) {
                    $original = Child-Path $backup $item.Relative
                    if ([IO.File]::Exists($item.Target) -and
                        (File-Digest $original) -eq (File-Digest $item.Target)) { continue }
                    Copy-Retry $original $item.Target
                }
                elseif ([IO.File]::Exists($item.Target)) { Remove-Item -LiteralPath $item.Target -Force }
            }
            $message = '更新失败，已恢复原版本。请重试。'
            $messageKey = 'UpdateRestored'
        } catch {
            $_ | Out-String | Add-Content -LiteralPath (Join-Path $jobDirectory 'error.log') -Encoding UTF8
            $message = '更新失败，原文件备份已保留。请重新安装消息岛。'
            $messageKey = 'UpdateBackupRetained'
            $canRestart = $false
        }
    }
} finally {
    if ($mutex) {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
    $parent.Dispose()
}

$result = @{ InstallDirectory = $install; Version = [string]$job.Version; Success = $success; Message = $message; MessageKey = $messageKey }
$resultPath = Join-Path $workRoot 'result.json'
$temporaryResult = Join-Path $jobDirectory 'result.json'
[IO.File]::WriteAllText($temporaryResult, ($result | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath $temporaryResult -Destination $resultPath -Force
if ($ElevatedWorker) {
    [IO.File]::WriteAllText((Join-Path $jobDirectory 'elevated-finished'), '', [Text.UTF8Encoding]::new($false))
}
if ($canRestart) {
    $arguments = if ($success -or $job.InstallerPath) { '--settings --after-update' } else { '--settings --show-update-result' }
    if ($job.PSObject.Properties['RestoreRegistration'] -and $job.RestoreRegistration -eq $true) {
        $arguments += ' --restore-registration'
    }
    Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory $install -WindowStyle Hidden
}
if ($success -and !$ElevatedWorker) {
    foreach ($name in @('payload', 'backup')) {
        $cleanup = Full-Path (Join-Path $jobDirectory $name)
        if (![IO.Path]::GetDirectoryName($cleanup).Equals($jobDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            throw '清理路径超出更新目录。'
        }
        Assert-NoLinks $cleanup
        if (Test-Path -LiteralPath $cleanup) {
            foreach ($child in @(Get-ChildItem -LiteralPath $cleanup -Recurse -Force)) { Assert-NoLinks $child.FullName }
            Remove-Item -LiteralPath $cleanup -Recurse -Force
        }
    }
    $names = @('update.zip', 'update.zip.sha256')
    if ($job.InstallerPath) {
        $installerName = [IO.Path]::GetFileName([string]$job.InstallerPath)
        $names += @($installerName, $installerName + '.sha256')
    }
    foreach ($name in $names) {
        $cleanup = Join-Path $jobDirectory $name
        if (Test-Path -LiteralPath $cleanup -PathType Leaf) { Remove-Item -LiteralPath $cleanup -Force }
    }
}
if (!$success) { exit 1 }
