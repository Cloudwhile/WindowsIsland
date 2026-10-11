using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WindowsIsland.Services;

internal sealed record RegisteredRestartJob(int ProcessId, long StartedUtcTicks, string Executable, string AppId, string Arguments);

internal static class RegisteredRestart
{
    public static async Task PrepareAsync(string appId, bool showSettings, bool showUpdateResult, bool verification = false)
    {
        using var parent = Process.GetCurrentProcess();
        var root = Path.Combine(Path.GetTempPath(), "WindowsIsland.Initialization");
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "Restart.ps1");
        var job = new RegisteredRestartJob(parent.Id, parent.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!, appId,
            (showSettings ? "--settings" : "") + (showUpdateResult ? " --show-update-result" : "") + (verification ? " --verify-local" : ""));
        await File.WriteAllTextAsync(Path.Combine(directory, "job.json"), JsonSerializer.Serialize(job));
        await File.WriteAllTextAsync(script, Script, new UTF8Encoding(true));
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", script })
            info.ArgumentList.Add(argument);
        using var helper = Process.Start(info) ?? throw new IOException("Registration restart helper did not start.");
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, "ready")))
            {
                if (helper.HasExited) throw new IOException("Registration restart helper failed its preflight.");
                if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Registration restart helper did not become ready.");
                await Task.Delay(100);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "commit"), "");
        }
        catch
        {
            try { if (!helper.HasExited) helper.Kill(); }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    // Activation must happen in a different executable, after the old application
    // exits. Calling ActivateApplication synchronously from its UI thread can
    // wait on the very instance that is still trying to finish initialization.
    internal const string Script = """
        $ErrorActionPreference = 'Stop'
        $stage = 'Preflight'
        $parentProcess = $null
        try {
            $job = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'job.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $parentProcess = Get-Process -Id $job.ProcessId -ErrorAction Stop
            if ($parentProcess.StartTime.ToUniversalTime().Ticks -ne $job.StartedUtcTicks -or
                ![string]::Equals($parentProcess.Path,$job.Executable,[StringComparison]::OrdinalIgnoreCase)) { throw 'Parent mismatch.' }
            $installation = [IO.Path]::GetDirectoryName($job.Executable)
            $registered = Get-AppxPackage | Where-Object { ($_.PackageFamilyName + '!App') -ceq $job.AppId -and
                [string]::Equals($_.InstallLocation.TrimEnd('\'),$installation.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase) }
            if (@($registered).Count -ne 1) { throw 'Registration mismatch.' }
            $arguments = @($job.Arguments.Split(' ',[StringSplitOptions]::RemoveEmptyEntries))
            if (@($arguments | Where-Object { $_ -notin @('--settings','--show-update-result','--verify-local') }).Count -gt 0 -or
                @($arguments | Select-Object -Unique).Count -ne $arguments.Count) { throw 'Unsupported arguments.' }
            Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class RegisteredActivation {
            [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IManager {
                [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id,
                    [MarshalAs(UnmanagedType.LPWStr)] string args,uint flags,out uint process);
            }
            public static void Launch(string id,string args) {
                var manager=(IManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
                try { uint process; Marshal.ThrowExceptionForHR(manager.ActivateApplication(id,args,2,out process)); }
                finally { Marshal.FinalReleaseComObject(manager); }
            }
        }
        '@
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'ready'),'')
            $stage = 'Commit'
            $deadline = [DateTime]::UtcNow.AddSeconds(35)
            while (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'commit'))) {
                if ($parentProcess.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Restart was not committed.' }
                Start-Sleep -Milliseconds 100
            }
            $stage = 'WaitForExit'
            if (!$parentProcess.WaitForExit(30000)) { throw 'Original application did not exit.' }
            $stage = 'Activate'
            [RegisteredActivation]::Launch($job.AppId,$job.Arguments)
        } catch {
            # Only fixed stage and exception type; no paths, arguments or message text.
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'failure.txt'),($stage + ':' + $_.Exception.GetType().FullName))
            exit 1
        } finally {
            if ($parentProcess) { $parentProcess.Dispose() }
            if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'failure.txt'))) {
                foreach ($name in @('ready','commit','job.json','Restart.ps1')) {
                    $path = Join-Path $PSScriptRoot $name
                    if (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
                }
                try { [IO.Directory]::Delete($PSScriptRoot) } catch { }
            }
        }
        """;
}
