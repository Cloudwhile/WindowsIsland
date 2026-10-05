using System.Diagnostics;
using System.Text.Json;
using WindowsIsland.Services;

internal static class UpdateScriptChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "update-script", Guid.NewGuid().ToString("N"));
        using (var fixture = new Fixture(Path.Combine(root, "success")))
        {
            await fixture.StartAsync();
            var result = await fixture.ResultAsync();
            check(result.Success && File.ReadAllText(fixture.Installed("a-library.dll")) == "new library",
                "The real update helper waits for the application to exit and installs staged files");
            check(File.ReadAllText(fixture.Installed("settings.json")) == "saved preferences"
                && File.ReadAllText(fixture.Installed("AppxManifest.xml")) == "existing registration"
                && File.ReadAllText(fixture.Installed("Uninstall.ps1")) == "existing uninstall script",
                "ZIP updates preserve preferences, registration and the MSI uninstall script");
            check(File.ReadAllText(fixture.Installed("resources.pri")) == "new resources" && !File.Exists(fixture.Installed("obsolete.txt"))
                && File.ReadAllText(fixture.Installed("user.txt")) == "user file", "Only previously managed obsolete files are removed and registered resources are refreshed");
            check((await fixture.RestartArgumentsAsync()).Contains("--after-update", StringComparison.Ordinal),
                "Successful updates restart the application with registration refresh and settings enabled");
            await WaitAsync(() => !Directory.Exists(fixture.Payload), "Successful update cleanup");
            check(!Directory.Exists(Path.Combine(fixture.Job, "backup")), "Successful updates clean up only their payload and backup directories");
        }
        using (var fixture = new Fixture(Path.Combine(root, "rollback")))
        {
            File.WriteAllText(fixture.Installed("z-locked.dll"), "locked original");
            File.WriteAllText(Path.Combine(fixture.Payload, "z-locked.dll"), "locked replacement");
            using var locked = new FileStream(fixture.Installed("z-locked.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
            await fixture.StartAsync();
            var result = await fixture.ResultAsync();
            check(!result.Success && result.Message.Contains("已恢复原版本", StringComparison.Ordinal)
                && File.ReadAllText(fixture.Installed("a-library.dll")) == "old library", "A locked installed module rolls back files already changed by the real helper");
            check(!File.Exists(fixture.Installed("new-file.txt")) && File.ReadAllText(fixture.Installed("obsolete.txt")) == "old managed file"
                && File.ReadAllText(fixture.Installed("resources.pri")) == "old resources", "Rollback removes new files and restores obsolete files and registered resources");
            check((await fixture.RestartArgumentsAsync()).Contains("--show-update-result", StringComparison.Ordinal),
                "Failed updates restart the restored application and show the failure result");
        }
        using (var fixture = new Fixture(Path.Combine(root, "conflict")))
        {
            Directory.CreateDirectory(fixture.Installed("z-conflict.dll"));
            File.WriteAllText(Path.Combine(fixture.Payload, "z-conflict.dll"), "replacement");
            var exited = await fixture.StartAsync(expectReady: false);
            check(exited != 0 && !File.Exists(Path.Combine(fixture.Job, "commit"))
                && File.ReadAllText(fixture.Installed("a-library.dll")) == "old library", "Preflight directory conflicts never commit or replace the running application");
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            while (!condition()) await Task.Delay(100, timeout.Token);
        }
        catch (OperationCanceledException) { throw new InvalidOperationException("Timed out: " + name); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _install;
        private readonly string _work;
        private Process? _application;
        public string Job { get; }
        public string Payload { get; }

        public Fixture(string directory)
        {
            _install = Path.Combine(directory, "installed with spaces");
            _work = Path.Combine(directory, "work");
            Job = Path.Combine(_work, Guid.NewGuid().ToString("N"));
            Payload = Path.Combine(Job, "payload");
            Directory.CreateDirectory(_install);
            foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(_install, Path.GetRelativePath(AppContext.BaseDirectory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            File.Copy(Path.Combine(AppContext.BaseDirectory, "WindowsIsland.LogicTests.exe"), Installed("WindowsIsland.exe"));
            var updater = Installed("Updater/Apply-Update.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(updater)!);
            File.Copy(Path.Combine(Environment.CurrentDirectory, "packaging", "Apply-Update.ps1"), updater);
            File.WriteAllText(Installed("a-library.dll"), "old library");
            File.WriteAllText(Installed("settings.json"), "saved preferences");
            File.WriteAllText(Installed("AppxManifest.xml"), "existing registration");
            File.WriteAllText(Installed("Uninstall.ps1"), "existing uninstall script");
            File.WriteAllText(Installed("resources.pri"), "old resources");
            File.WriteAllText(Installed("obsolete.txt"), "old managed file");
            File.WriteAllText(Installed("user.txt"), "user file");
            File.WriteAllText(Installed(".update-files.json"), "[\"obsolete.txt\",\"a-library.dll\"]");
            Directory.CreateDirectory(Payload);
            File.Copy(Installed("WindowsIsland.exe"), Path.Combine(Payload, "WindowsIsland.exe"));
            File.WriteAllText(Path.Combine(Payload, "a-library.dll"), "new library");
            File.WriteAllText(Path.Combine(Payload, "new-file.txt"), "new file");
            File.WriteAllText(Path.Combine(Payload, "WindowsIsland.pri"), "new resources");
            File.WriteAllText(Path.Combine(Payload, "settings.json"), "replacement preferences");
            File.WriteAllText(Path.Combine(Payload, "AppxManifest.xml"), "replacement manifest");
            File.WriteAllText(Path.Combine(Payload, "Uninstall.ps1"), "replacement uninstall script");
        }

        public string Installed(string path) => Path.Combine(_install, path);

        public async Task<int> StartAsync(bool expectReady = true)
        {
            var start = new ProcessStartInfo(Installed("WindowsIsland.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _install, RedirectStandardError = true, RedirectStandardOutput = true
            };
            start.ArgumentList.Add("--update-fixture");
            start.ArgumentList.Add(Job);
            _application = Process.Start(start)!;
            var errors = _application.StandardError.ReadToEndAsync();
            var output = _application.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await _application.WaitForExitAsync(timeout.Token);
            var detail = await errors + await output;
            if (expectReady && _application.ExitCode != 0) throw new InvalidOperationException("Update handoff failed: " + detail);
            return _application.ExitCode;
        }

        public async Task<UpdateResult> ResultAsync()
        {
            var path = Path.Combine(_work, "result.json");
            await WaitAsync(() => File.Exists(path), "Update result");
            return JsonSerializer.Deserialize<UpdateResult>(await File.ReadAllTextAsync(path))!;
        }

        public async Task<string> RestartArgumentsAsync()
        {
            var path = Installed("restart.args");
            await WaitAsync(() => File.Exists(path), "Application restart");
            return await File.ReadAllTextAsync(path);
        }

        public void Dispose()
        {
            if (_application is null) return;
            if (!_application.HasExited) _application.Kill();
            _application.Dispose();
        }
    }
}
