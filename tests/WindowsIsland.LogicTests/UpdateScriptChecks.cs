using System.Diagnostics;
using System.Runtime.InteropServices;
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
        using (var fixture = new Fixture(Path.Combine(root, "gui-success"), gui: true, restoreRegistration: true))
        {
            await fixture.StartAsync();
            var result = await fixture.ResultAsync();
            check(result.Success && File.ReadAllText(fixture.Installed("a-library.dll")) == "new library",
                "A GUI application without redirected console streams hands ZIP updates to the real helper");
            check((await fixture.RestartArgumentsAsync()).Contains("--after-update", StringComparison.Ordinal),
                "Updates started by a GUI application restart it after replacement");
            check((await fixture.RestartArgumentsAsync()).Contains("--restore-registration", StringComparison.Ordinal),
                "ZIP update handoff preserves registered activation independently of notification preferences");
        }
        using (var fixture = new Fixture(Path.Combine(root, "gui-msi-success"), 0, gui: true, restoreRegistration: true))
        {
            await fixture.StartAsync();
            var result = await fixture.ResultAsync();
            check(result.Success && !Directory.Exists(fixture.Payload)
                && File.ReadAllText(fixture.Installed("a-library.dll")) == "new MSI library",
                "A GUI application without redirected console streams hands MSI updates to the real helper");
            check((await fixture.RestartArgumentsAsync()).Contains("--restore-registration", StringComparison.Ordinal),
                "MSI update handoff preserves registered activation after installation");
        }
        foreach (var msi in new[] { false, true })
        using (var fixture = new Fixture(Path.Combine(root, msi ? "junction-msi" : "junction-zip"),
            msi ? 0 : null, gui: true, junctionWork: true))
        {
            check(UpdateWorkDirectory.Resolve(fixture.Work) == fixture.PhysicalWork
                && !Directory.EnumerateFiles(fixture.PhysicalWork, ".path-*").Any(),
                "Resolving a junction work root returns its physical directory and removes its probe");
            await fixture.StartAsync();
            var result = await fixture.ResultAsync();
            var job = JsonSerializer.Deserialize<UpdateJob>(await File.ReadAllTextAsync(Path.Combine(fixture.PhysicalJob, "job.json")))!;
            check(result.Success && job.PayloadDirectory == Path.Combine(fixture.PhysicalJob, "payload")
                && job.InstallerPath == (msi ? Path.Combine(fixture.PhysicalJob, "update.msi") : null)
                && File.Exists(Path.Combine(fixture.PhysicalJob, "ready"))
                && File.Exists(Path.Combine(fixture.PhysicalJob, "commit")),
                "A GUI application hands physically resolved junction job paths to the real helper: " + (msi ? "MSI" : "ZIP"));
            await fixture.RestartArgumentsAsync();
            if (!msi) await WaitAsync(() => !Directory.Exists(fixture.Payload), "Junction update cleanup");
            check(!Directory.EnumerateFiles(fixture.PhysicalJob, ".path-*").Any(),
                "Resolving a junction job leaves no temporary probe after update handoff");
        }
        using (var fixture = new Fixture(Path.Combine(root, "rollback"), restoreRegistration: true))
        {
            File.WriteAllText(fixture.Installed("z-locked.dll"), "locked original");
            File.WriteAllText(Path.Combine(fixture.Payload, "z-locked.dll"), "locked replacement");
            using var locked = new FileStream(fixture.Installed("z-locked.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
            await fixture.StartAsync();
            var result = await fixture.ResultAsync();
            check(!result.Success && result.MessageKey == "UpdateRestored" && result.Message.Contains("已恢复原版本", StringComparison.Ordinal)
                && File.ReadAllText(fixture.Installed("a-library.dll")) == "old library", "A locked installed module rolls back files already changed by the real helper");
            check(!File.Exists(fixture.Installed("new-file.txt")) && File.ReadAllText(fixture.Installed("obsolete.txt")) == "old managed file"
                && File.ReadAllText(fixture.Installed("resources.pri")) == "old resources", "Rollback removes new files and restores obsolete files and registered resources");
            check((await fixture.RestartArgumentsAsync()).Contains("--show-update-result", StringComparison.Ordinal),
                "Failed updates restart the restored application and show the failure result");
            check((await fixture.RestartArgumentsAsync()).Contains("--restore-registration", StringComparison.Ordinal),
                "ZIP rollback preserves registered activation so the restored application can read its result");
        }
        using (var fixture = new Fixture(Path.Combine(root, "conflict")))
        {
            Directory.CreateDirectory(fixture.Installed("z-conflict.dll"));
            File.WriteAllText(Path.Combine(fixture.Payload, "z-conflict.dll"), "replacement");
            var exited = await fixture.StartAsync(expectReady: false);
            check(exited != 0 && !File.Exists(Path.Combine(fixture.Job, "commit"))
                && File.ReadAllText(fixture.Installed("a-library.dll")) == "old library", "Preflight directory conflicts never commit or replace the running application");
            check(File.ReadAllText(fixture.FailureLog).Contains("已有目录冲突", StringComparison.Ordinal),
                "Preflight failure details survive disposal of the failed update job");
        }
        using (var fixture = new Fixture(Path.Combine(root, "helper-start-failure")))
        {
            File.WriteAllText(fixture.Installed("Updater/Apply-Update.ps1"),
                "param([string]$JobPath)\n[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\nthrow '模拟更新启动失败。'\n");
            var exited = await fixture.StartAsync(expectReady: false);
            check(exited != 0 && !File.Exists(Path.Combine(fixture.Job, "commit"))
                && File.ReadAllText(fixture.FailureLog).Contains("模拟更新启动失败", StringComparison.Ordinal),
                "Early helper crashes retain bounded UTF-8 diagnostics and never request application exit");
        }
        foreach (var code in new[] { 0, 3010, 1603 })
        using (var fixture = new Fixture(Path.Combine(root, "msi-" + code), code))
        {
            File.WriteAllText(fixture.Installed(".update-files.json"), "broken ZIP inventory");
            await fixture.StartAsync();
            var result = await fixture.ResultAsync();
            var arguments = File.ReadAllText(fixture.Installed("msi.args"));
            var success = code is 0 or 3010;
            check(result.Success == success && !Directory.Exists(fixture.Payload)
                && arguments.Contains(Path.Combine(fixture.Job, "update.msi"), StringComparison.Ordinal)
                && arguments.Contains("/qn /norestart", StringComparison.Ordinal)
                && arguments.Contains("INSTALLFOLDER=" + Path.GetDirectoryName(fixture.Installed("WindowsIsland.exe")), StringComparison.Ordinal),
                "The real MSI helper hands off without ZIP payload or inventory and invokes the simulated installer: " + code);
            check(File.ReadAllText(fixture.Installed("a-library.dll")) == (success ? "new MSI library" : "old library")
                && File.ReadAllText(fixture.Installed("settings.json")) == "saved preferences"
                && (await fixture.RestartArgumentsAsync()).Contains("--after-update", StringComparison.Ordinal),
                "MSI helper success, reboot-required and failure retain preferences and refresh app registration on restart: " + code);
        }
        foreach (var code in new[] { 0, 3010 })
        using (var fixture = new Fixture(Path.Combine(root, "msi-unchanged-" + code), code))
        {
            File.WriteAllText(Path.Combine(fixture.Job, "expected-version.txt"), "99.0.0");
            await fixture.StartAsync();
            var result = await fixture.ResultAsync();
            check(!result.Success && result.MessageKey == "UpdateFailed" && File.Exists(Path.Combine(fixture.Job, "update.msi")),
                "MSI success codes cannot claim success when installed binaries retain the old version: " + code);
            check((await fixture.RestartArgumentsAsync()).Contains("--after-update", StringComparison.Ordinal),
                "MSI version mismatch still restores notification registration on restart: " + code);
        }
        using (var fixture = new Fixture(Path.Combine(root, "msi-missing-assembly"), 0))
        {
            File.Delete(fixture.Installed("WindowsIsland.dll"));
            await fixture.StartAsync();
            check(!(await fixture.ResultAsync()).Success,
                "A matching executable cannot hide a missing application assembly after MSI installation");
            await fixture.RestartArgumentsAsync();
        }
        using (var fixture = new Fixture(Path.Combine(root, "zip-version-mismatch")))
        {
            File.WriteAllText(Path.Combine(fixture.Job, "expected-version.txt"), "99.0.0");
            await fixture.StartAsync();
            var result = await fixture.ResultAsync();
            check(!result.Success && result.MessageKey == "UpdateRestored"
                && File.ReadAllText(fixture.Installed("a-library.dll")) == "old library",
                "ZIP version mismatch rolls the replaced files back before reporting failure");
            await fixture.RestartArgumentsAsync();
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
        private readonly bool _junctionWork;
        private readonly bool _gui;
        private readonly bool _restoreRegistration;
        private Process? _application;
        public string Job { get; }
        public string Payload { get; }
        public string Work => _work;
        public string PhysicalWork { get; }
        public string PhysicalJob => Path.Combine(PhysicalWork, Path.GetFileName(Job));
        public string FailureLog => Path.Combine(_work, "last-helper-error.log");

        public Fixture(string directory, int? installerExitCode = null, bool gui = false, bool junctionWork = false,
            bool restoreRegistration = false)
        {
            _gui = gui;
            _restoreRegistration = restoreRegistration;
            _junctionWork = junctionWork;
            _install = Path.Combine(directory, "installed with spaces");
            _work = Path.Combine(directory, "work");
            PhysicalWork = junctionWork ? Path.Combine(directory, "physical work") : _work;
            if (junctionWork)
            {
                Directory.CreateDirectory(PhysicalWork);
                CreateJunction(_work, PhysicalWork);
            }
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
            if (gui) MakeGuiApplication(Installed("WindowsIsland.exe"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "WindowsIsland.LogicTests.dll"), Installed("WindowsIsland.dll"));
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
            if (installerExitCode is { } code)
            {
                Directory.CreateDirectory(Job);
                File.WriteAllText(Path.Combine(Job, "update.msi"), "simulated verified MSI");
                File.Copy(Installed("WindowsIsland.exe"), Installed("msiexec-fixture.exe"));
                File.WriteAllText(Installed("msi-fixture-exit.txt"), code.ToString());
                var script = File.ReadAllText(updater)
                    .Replace("$registration = Get-ItemProperty -LiteralPath 'HKCU:\\Software\\Cloudwhile\\WindowsIsland\\Installer'",
                        "$registration = [pscustomobject]@{ InstallDirectory = $install }", StringComparison.Ordinal)
                    .Replace("$start.FileName = Join-Path ([Environment]::GetFolderPath('System')) 'msiexec.exe'",
                        "$start.FileName = Join-Path $install 'msiexec-fixture.exe'", StringComparison.Ordinal);
                File.WriteAllText(updater, script);
                return;
            }
            Directory.CreateDirectory(Payload);
            File.Copy(Installed("WindowsIsland.exe"), Path.Combine(Payload, "WindowsIsland.exe"));
            File.Copy(Installed("WindowsIsland.dll"), Path.Combine(Payload, "WindowsIsland.dll"));
            File.WriteAllText(Path.Combine(Payload, "a-library.dll"), "new library");
            File.WriteAllText(Path.Combine(Payload, "new-file.txt"), "new file");
            File.WriteAllText(Path.Combine(Payload, "WindowsIsland.pri"), "new resources");
            File.WriteAllText(Path.Combine(Payload, "settings.json"), "replacement preferences");
            File.WriteAllText(Path.Combine(Payload, "AppxManifest.xml"), "replacement manifest");
            File.WriteAllText(Path.Combine(Payload, "Uninstall.ps1"), "replacement uninstall script");
        }

        public string Installed(string path) => Path.Combine(_install, path);

        private static void CreateJunction(string link, string target)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0 || (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
                throw new InvalidOperationException("Creating the isolated update junction failed: "
                    + output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
        }

        private static void MakeGuiApplication(string path)
        {
            var image = File.ReadAllBytes(path);
            if (image.Length < 64 || BitConverter.ToUInt16(image, 0) != 0x5a4d)
                throw new InvalidDataException("The GUI update fixture requires a PE apphost.");
            var pe = BitConverter.ToInt32(image, 0x3c);
            if (pe < 0 || pe > image.Length - 94 || BitConverter.ToUInt32(image, pe) != 0x4550
                || BitConverter.ToUInt16(image, pe + 20) < 70
                || BitConverter.ToUInt16(image, pe + 24) is not (0x10b or 0x20b))
                throw new InvalidDataException("The GUI update fixture has an invalid PE header.");
            // The Subsystem field has the same offset in PE32 and PE32+ headers.
            image[pe + 24 + 68] = 2;
            image[pe + 24 + 69] = 0;
            File.WriteAllBytes(path, image);
        }

        public async Task<int> StartAsync(bool expectReady = true)
        {
            var start = new ProcessStartInfo(Installed("WindowsIsland.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _install, RedirectStandardError = !_gui, RedirectStandardOutput = !_gui
            };
            start.ArgumentList.Add("--update-fixture");
            start.ArgumentList.Add(Job);
            if (_gui) start.ArgumentList.Add("--no-console");
            if (_restoreRegistration) start.ArgumentList.Add("--restore-registration");
            _application = Process.Start(start)!;
            var errors = _gui ? Task.FromResult("") : _application.StandardError.ReadToEndAsync();
            var output = _gui ? Task.FromResult("") : _application.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await _application.WaitForExitAsync(timeout.Token);
            var detail = await errors + await output;
            if (_gui && _application.ExitCode != 0 && File.Exists(FailureLog))
                detail = await File.ReadAllTextAsync(FailureLog);
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
            if (_application is not null)
            {
                if (!_application.HasExited) _application.Kill();
                _application.Dispose();
            }
            if (_junctionWork && Directory.Exists(_work)) Directory.Delete(_work);
        }
    }
}

internal static class UpdateFixtureConsole
{
    public static void Detach()
    {
        // GUI apphosts can still inherit console handles from the test runner.
        // Remove both console attachment and all inherited standard handles.
        _ = FreeConsole();
        foreach (var handle in new[] { -10, -11, -12 })
        {
            if (!SetStdHandle(handle, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int handle, IntPtr value);
}
