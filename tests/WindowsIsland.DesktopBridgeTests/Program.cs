using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;

if (args is ["--target", var mode])
{
    if (mode == "query-denied")
    {
        // Permit QUERY/SYNCHRONIZE identity checks while denying PROCESS_VM_READ for EnumProcessModules.
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;0x101400;;;{sid})", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!SetKernelObjectSecurity(GetCurrentProcess(), 4, descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { LocalFree(descriptor); }
    }
    Console.WriteLine("READY");
    Console.Out.Flush();
    Console.ReadLine();
    return;
}

foreach (var scenario in new[] { "retained", "absent", "exited", "query-denied" })
{
    using var target = StartTarget(scenario);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var ready = await target.StandardOutput.ReadLineAsync(deadline.Token);
    Check(ready == "READY", "Target must reach its controlled ready state: " + scenario);
    using var clientProcess = Process.GetProcessById(target.Id);
    var startedAt = clientProcess.StartTime.ToUniversalTime().Ticks;
    // Retain the OS process handle before exit so identity never resolves through a reused PID.
    Check(!clientProcess.HasExited, "Original target process handle is retained before teardown");
    var library = scenario == "absent" ? Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
    if (scenario == "retained")
        Check(clientProcess.Modules.Cast<ProcessModule>().Any(module => string.Equals(module.FileName, library, StringComparison.OrdinalIgnoreCase)),
            "Fixture retains the real native module throughout teardown");
    var broker = new PluginDesktopBroker(new("wini_plugin_telegram", "fixture", "0.1.0", "fixture.dll", "Fixture", 1,
        ["notifications", "automation", "process"], DeveloperId: "winiofficial",
        RepositoryUrl: "https://github.com/WinIOfficial/wini_plugin_telegram", SourceKind: PluginSourceKind.Official)
        { Desktop = new(["Telegram"], ["kernel32.dll"], "Synthetic teardown regression") }, AppContext.BaseDirectory);
    // Populate the real session/client ownership boundary without injecting any code into the synthetic target.
    var sessionType = typeof(PluginDesktopBroker).GetNestedType("BridgeSession", BindingFlags.NonPublic)!;
    var session = (IDisposable)Activator.CreateInstance(sessionType, clientProcess, library)!;
    sessionType.GetField("Attached")!.SetValue(session, true);
    ((IDictionary)Field(broker, "_bridges")).Add(target.Id, session);
    var clientType = typeof(PluginDesktopBroker).GetNestedType("Client", BindingFlags.NonPublic)!;
    var handle = new SafeProcessHandle(nint.Zero, false);
    var client = Activator.CreateInstance(clientType, clientProcess, handle, startedAt)!;
    ((IDictionary)Field(broker, "_clients")).Add(target.Id, client);
    // A second client proves the first bridge failure cannot abort remaining cleanup.
    using var otherProcess = Process.GetCurrentProcess();
    var otherHandle = new SafeProcessHandle(nint.Zero, false);
    var otherClient = Activator.CreateInstance(clientType, otherProcess, otherHandle, otherProcess.StartTime.ToUniversalTime().Ticks)!;
    ((IDictionary)Field(broker, "_clients")).Add(otherProcess.Id, otherClient);
    typeof(PluginDesktopBroker).GetField("_processIds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(broker, new[] { target.Id });
    Check(broker.OwnsProcess(target.Id), "Fixture begins with live process authorization");
    if (scenario == "exited")
    {
        var exited = target.WaitForExitAsync(deadline.Token);
        await target.StandardInput.WriteLineAsync();
        await exited;
    }
    if (scenario == "query-denied")
    {
        Check(!target.HasExited, "Query-denied target remains alive");
        var denied = false;
        try { _ = clientProcess.Modules; }
        catch (Win32Exception) { denied = true; }
        // SeDebugPrivilege on verification guests can bypass the fixture DACL, same as installer terminate probes.
        if (!denied)
        {
            Console.WriteLine("SKIP query-denied: module enumeration could not be denied under current privileges");
            broker.Dispose();
            try { await broker.DisposeAsync().AsTask().WaitAsync(deadline.Token); } catch (AggregateException) { }
            await target.StandardInput.WriteLineAsync();
            await target.WaitForExitAsync(deadline.Token);
            continue;
        }
        Check(true, "Fixture denies actual module enumeration");
    }
    // Subscribe to child exit before triggering teardown; finally always releases our controlled target.
    var targetExit = target.WaitForExitAsync(deadline.Token);
    try
    {
        broker.Dispose();
        Check(!broker.OwnsProcess(target.Id), "Dispose immediately revokes process authorization");
        Exception? failure = null;
        try { await broker.DisposeAsync().AsTask().WaitAsync(deadline.Token); }
        catch (AggregateException error) { failure = error.Flatten(); }
        var shouldFail = scenario is "retained" or "query-denied";
        Check((failure is not null) == shouldFail, scenario + ": DisposeAsync confirms absence/exit and rejects retained or unqueryable modules");
        if (scenario == "retained") Check(((AggregateException)failure!).InnerExceptions.Any(error => error is TimeoutException), "Retained native module reports unload timeout");
        if (scenario == "query-denied") Check(((AggregateException)failure!).InnerExceptions.Any(error => error is IOException { InnerException: Win32Exception }), "Enumeration denial reports unconfirmed unload");
        Check(handle.IsClosed && otherHandle.IsClosed, "Every client handle closes even when the first bridge fails");
        if (shouldFail)
        {
            var repeatedFailure = false;
            try { session.Dispose(); } catch (Exception error) when (error is TimeoutException or IOException) { repeatedFailure = true; }
            Check(repeatedFailure, "Repeated teardown cannot turn an unconfirmed unload into success");
        }
    }
    finally
    {
        // The controller's wrapper stays separate from the Process owned by DisposeClient.
        if (scenario != "exited")
        {
            await target.StandardInput.WriteLineAsync();
            await targetExit;
        }
    }
}
Console.WriteLine("PASS desktop bridge teardown regression");

static Process StartTarget(string mode)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
    start.ArgumentList.Add("--target"); start.ArgumentList.Add(mode);
    return Process.Start(start)!;
}
static object Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}
[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out nint descriptor, out uint size);
[DllImport("advapi32.dll", SetLastError = true)] static extern bool SetKernelObjectSecurity(nint handle, uint information, nint descriptor);
[DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
[DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);
