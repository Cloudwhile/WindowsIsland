using System.Text.Json;
using WindowsIsland.Services;

internal static class DiagnosticChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "diagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.UtcNow;
        using var store = new DiagnosticStore(Path.Combine(root, "records"), true, () => now);
        var error = PrivateError();
        var id = store.Record(DiagnosticArea.WeChat, "ReadConversations", error);
        var first = store.Snapshot().Single();
        check(id?.Length == 12 && first.Area == DiagnosticArea.WeChat && first.Errors[0].HResult == error.HResult,
            "Diagnostic reports identify an error, component and native error code");
        check(first.Errors[0].Frames.Any(frame => frame.Method.Contains(nameof(PrivateError)) && frame.Token != 0 && frame.Module.Length == 32),
            "Diagnostic locations retain method metadata and module identity without source paths");
        var json = store.ReportJson();
        check(DiagnosticReports.AppVersion == ReleaseVersion.Current.Tag && first.AppVersion == ReleaseVersion.Current.Tag,
            "Diagnostic entries and reports use the same full release tag as the application");
        check(!new[] { "PRIVATE_CHAT", "PRIVATE_ACCOUNT", "secret-token", "Users", "credentials", "raw-source", "private-key" }.Any(json.Contains),
            "Messages, paths, credentials, exception Data and exception Source are excluded from diagnostic export");
        store.Record(DiagnosticArea.WeChat, "ReadConversations", error);
        check(store.Snapshot() is [{ Count: 2 }] && store.Snapshot()[0].Id == id,
            "Repeated errors retain one diagnostic ID and increment their occurrence count");
        store.Record(DiagnosticArea.Updates, "ReadConversations", error);
        check(store.Snapshot().Length == 2, "The same exception in different components remains distinguishable");
        check(store.Record(DiagnosticArea.Updates, "Cancel", new OperationCanceledException()) is null,
            "Expected cancellation is not reported as a failure");
        store.SetEnabled(false);
        check(store.Record(DiagnosticArea.Settings, "Disabled", error) is null && store.Snapshot().Length == 2,
            "Disabling local diagnostics stops collection and retains existing user reports");
        var export = Path.Combine(root, "report.json");
        await store.ExportAsync(export);
        var report = JsonSerializer.Deserialize<DiagnosticReport>(await File.ReadAllTextAsync(export), DiagnosticStore.Json)!;
        check(report.SchemaVersion == 1 && report.Events.Length == 2 && report.Events.All(entry => entry.AppVersion == DiagnosticReports.AppVersion),
            "Export retains occurrence-time application versions and a versioned report schema");
        store.Flush();
        using (var reopened = new DiagnosticStore(Path.Combine(root, "records"), false, () => now))
            check(reopened.Snapshot().Length == 2, "Local records survive a restart even when recording has been disabled");
        var retiredArea = Path.Combine(root, "retired-area");
        Directory.CreateDirectory(retiredArea);
        var retiredEntry = JsonSerializer.SerializeToNode(first, DiagnosticStore.Json)!;
        retiredEntry[nameof(DiagnosticEntry.Area)] = "retired-component";
        File.WriteAllText(Path.Combine(retiredArea, "diagnostics.json"), new System.Text.Json.Nodes.JsonArray(
            retiredEntry, JsonSerializer.SerializeToNode(first, DiagnosticStore.Json)).ToJsonString(DiagnosticStore.Json));
        using (var loaded = new DiagnosticStore(retiredArea, false, () => now))
            check(!loaded.PersistenceFailed && loaded.Snapshot() is [{ Area: DiagnosticArea.WeChat }],
                "A retired diagnostic area is skipped without discarding remaining valid records");
        var before = await File.ReadAllTextAsync(export);
        try { await store.ExportAsync(export, new CancellationToken(true)); }
        catch (OperationCanceledException) { }
        check(await File.ReadAllTextAsync(export) == before && Directory.GetFiles(root, "report.json.*.tmp").Length == 0,
            "Cancelled exports preserve an existing file and remove temporary output");
        try { await store.ExportAsync(Path.Combine(root, "records", "diagnostics.json")); check(false, "Internal store destination rejected"); }
        catch (IOException) { check(true, "Exports cannot overwrite the internal diagnostic store"); }
        store.Clear();
        check(store.Snapshot().Length == 0 && File.Exists(export), "Clearing local records preserves previously exported reports");
        store.Flush();
        using (var reopened = new DiagnosticStore(Path.Combine(root, "records"), true, () => now))
            check(reopened.Snapshot().Length == 0, "A queued persistence operation cannot restore cleared records");
        store.SetEnabled(true);
        Parallel.For(0, 100, _ => store.Record(DiagnosticArea.WeChat, "Concurrent", error));
        check(store.Snapshot() is [{ Count: 100 }], "Concurrent reports are merged without losing occurrences");
        for (var i = 0; i < 250; i++) store.Record(DiagnosticArea.Settings, "Case" + i, error);
        check(store.Snapshot().Length == DiagnosticStore.MaximumEntries, "The diagnostic event ring is bounded");
        store.Flush();
        check(new FileInfo(Path.Combine(root, "records", "diagnostics.json")).Length <= DiagnosticStore.MaximumBytes
            && System.Text.Encoding.UTF8.GetByteCount(store.ReportJson()) <= DiagnosticStore.MaximumBytes,
            "Stored diagnostics and export payloads remain size bounded");
        now = now.AddDays(8);
        check(store.Snapshot().Length == 0, "Expired diagnostic events are removed after seven days");
        store.Record(DiagnosticArea.Application, "Crash", error, fatal: true);
        using (var reopened = new DiagnosticStore(Path.Combine(root, "records"), true, () => now))
            check(reopened.Snapshot() is [{ Fatal: true }], "Fatal errors are persisted before returning from the crash handler");
        var corrupted = Path.Combine(root, "corrupted");
        Directory.CreateDirectory(corrupted);
        File.WriteAllText(Path.Combine(corrupted, "diagnostics.json"), "{broken}");
        using (var invalid = new DiagnosticStore(corrupted, true))
            check(invalid.PersistenceFailed && invalid.Snapshot().Length == 0, "A corrupted diagnostic file cannot prevent application startup");
        var edited = first with { Operation = "https://private.example/secret-token" };
        File.WriteAllText(Path.Combine(corrupted, "diagnostics.json"), JsonSerializer.Serialize(new[] { edited }, DiagnosticStore.Json));
        using (var invalid = new DiagnosticStore(corrupted, true))
            check(invalid.Snapshot().Length == 0, "Unexpected edited report fields are not forwarded into exports");
        var blocked = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocked, "occupied");
        using (var unavailable = new DiagnosticStore(blocked, true))
        {
            unavailable.Record(DiagnosticArea.Application, "CannotPersist", error, fatal: true);
            check(unavailable.PersistenceFailed && unavailable.Snapshot().Length == 1,
                "Storage failures preserve in-memory diagnostics without replacing the original error");
        }
        var uri = DiagnosticReports.FeedbackUri();
        check(uri.Scheme == "https" && uri.Host == "github.com" && uri.Query == "?template=bug_report.yml",
            "The feedback browser URL contains no diagnostic records or private data");
        var settingsFile = Path.Combine(root, "settings.json");
        var settings = new SettingsStore(settingsFile);
        settings.Save(settings.Current with { LocalDiagnostics = false, Language = "en-US" });
        check(!new SettingsStore(settingsFile).Current.LocalDiagnostics, "The local diagnostic preference survives restart");
        File.WriteAllText(settingsFile, "{\"LocalDiagnostics\":\"broken\",\"Language\":\"en-US\",\"WeChat\":false}");
        var recovered = new SettingsStore(settingsFile).Current;
        check(recovered.LocalDiagnostics && !recovered.WeChat && recovered.Language == "en-US",
            "A damaged diagnostic preference preserves unrelated settings");
    }

    private static Exception PrivateError()
    {
        try { throw new IOException("PRIVATE_CHAT PRIVATE_ACCOUNT secret-token C:\\Users\\private\\credentials"); }
        catch (Exception error)
        {
            error.Data["private-key"] = "PRIVATE_ACCOUNT";
            error.Source = "raw-source";
            return error;
        }
    }
}
