using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WindowsIsland.Services;

internal static class RegisteredRestartChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "original.pri");
        var destination = Path.Combine(root, "resources.pri");
        File.WriteAllText(source, "initialization resources");
        InitializationResources.Prepare(source, destination);
        check(File.ReadAllText(destination) == "initialization resources", "First registration prepares its resources");
        using (var mapped = File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            InitializationResources.Prepare(source, destination);
            check(mapped.Length > 0, "Initialization retry keeps identical locked resources instead of rewriting them");
            File.WriteAllText(source, "new initialization resources");
            var rejected = false;
            try { InitializationResources.Prepare(source, destination); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
            check(rejected && File.ReadAllText(destination) == "initialization resources"
                && Directory.GetFiles(root, "*.tmp").Length == 0, "Failed resource replacement preserves the original and cleans up its temporary file");
        }
        InitializationResources.Prepare(source, destination);
        check(File.ReadAllText(destination) == "new initialization resources", "Changed resources replace an unlocked old file");
        var executable = Path.Combine(AppContext.BaseDirectory, "WindowsIsland.LogicTests.exe");
        foreach (var invalidParent in new[] { false, true })
        {
            var directory = Path.Combine(root, invalidParent ? "invalid-parent" : "success");
            Directory.CreateDirectory(directory);
            var parentReady = Path.Combine(directory, "parent-ready");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--restart-parent-fixture"); start.ArgumentList.Add(parentReady);
            using var parent = Process.Start(start)!;
            Process? helper = null;
            try
            {
                await WaitAsync(() => File.Exists(parentReady));
                var job = new RegisteredRestartJob(parent.Id, parent.StartTime.ToUniversalTime().Ticks + (invalidParent ? 1 : 0),
                    executable, "WindowsIsland.Test!App", "--settings --show-update-result --verify-local");
                await File.WriteAllTextAsync(Path.Combine(directory, "job.json"), JsonSerializer.Serialize(job));
                // Substitute only OS package discovery and the final activation. The
                // real PowerShell helper still validates, handshakes and waits for exit.
                var script = "function Get-AppxPackage { [pscustomobject]@{PackageFamilyName='WindowsIsland.Test';InstallLocation=[IO.Path]::GetDirectoryName($job.Executable)} }\n"
                    + RegisteredRestart.Script.Replace("[RegisteredActivation]::Launch($job.AppId,$job.Arguments)",
                        "[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'activated'),$job.Arguments)", StringComparison.Ordinal);
                var path = Path.Combine(directory, "Restart.ps1");
                await File.WriteAllTextAsync(path, script, new UTF8Encoding(true));
                var launch = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) launch.ArgumentList.Add(arg);
                helper = Process.Start(launch)!;
                if (invalidParent)
                {
                    await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    check(helper.ExitCode != 0 && !File.Exists(Path.Combine(directory, "ready")) && !parent.HasExited,
                        "Registration restart rejects a reused or mismatched parent without stopping it");
                    check(File.ReadAllText(Path.Combine(directory, "failure.txt")).StartsWith("Preflight:", StringComparison.Ordinal),
                        "Restart preflight failures preserve only a fixed stage and exception type");
                }
                else
                {
                    await WaitAsync(() => File.Exists(Path.Combine(directory, "ready")) || helper.HasExited);
                    check(!helper.HasExited && !File.Exists(Path.Combine(directory, "activated")),
                        "Registration restart helper signals readiness without activating the live application");
                    await File.WriteAllTextAsync(Path.Combine(directory, "commit"), "");
                    await Task.Delay(250);
                    check(!File.Exists(Path.Combine(directory, "activated")), "Committed restart still waits for the original process to exit");
                    parent.Kill();
                    await parent.WaitForExitAsync();
                    await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    check(helper.ExitCode == 0 && File.ReadAllText(Path.Combine(directory, "activated")) == job.Arguments,
                        "Restart activates after exit and preserves settings, update-result and verification arguments");
                    check(!File.Exists(path) && !File.Exists(Path.Combine(directory, "job.json")),
                        "Successful registration restart cleans up its script and job files");
                }
            }
            finally
            {
                if (!parent.HasExited) { parent.Kill(); await parent.WaitForExitAsync(); }
                if (helper is not null) { if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync(); } helper.Dispose(); }
            }
        }
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Restart fixture did not become ready.");
            await Task.Delay(50);
        }
    }
}
