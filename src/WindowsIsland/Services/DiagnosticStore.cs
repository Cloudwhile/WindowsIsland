using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using WindowsIsland.Contracts;

namespace WindowsIsland.Services;

internal sealed class DiagnosticStore : IDisposable
{
    internal const int MaximumEntries = 200;
    internal const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<DiagnosticEntry> _entries = [];
    private readonly Timer _writer;
    private bool _enabled, _disposed, _dirty;
    public bool PersistenceFailed { get; private set; }
    public bool Enabled { get { lock (_gate) return _enabled; } }
    public DiagnosticReportCache Reports { get; }

    public DiagnosticStore(string directory, bool enabled, Func<DateTimeOffset>? clock = null)
    {
        _path = Path.Combine(directory, "diagnostics.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _enabled = enabled;
        Reports = new(ReportJson);
        _writer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    public string? Record(DiagnosticArea area, string operation, Exception? error = null, bool fatal = false, string? clientVersion = null) =>
        RecordCore(area, operation, error, fatal, clientVersion);

    public string? RecordPlugin(string pluginId, DiagnosticArea area, string operation, Exception? error = null,
        bool fatal = false, PluginDiagnosticInfo? information = null) =>
        RecordCore(area, operation, error, fatal, null, DiagnosticReports.Identifier(pluginId, 80), information);

    private string? RecordCore(DiagnosticArea area, string operation, Exception? error, bool fatal, string? clientVersion,
        string? pluginId = null, PluginDiagnosticInfo? information = null)
    {
        try
        {
            lock (_gate)
            {
                if (!_enabled || _disposed || error is OperationCanceledException) return null;
                var now = _clock();
                var entry = DiagnosticReports.Capture(area, operation, error, fatal, now, clientVersion, pluginId, information);
                Prune(now);
                var existing = _entries.FindIndex(item => item.Fingerprint == entry.Fingerprint);
                if (existing >= 0)
                {
                    var previous = _entries[existing];
                    entry = previous with { LastSeen = now, Count = Math.Min(previous.Count + 1, 1_000_000),
                        ClientVersion = entry.ClientVersion ?? previous.ClientVersion };
                    _entries.RemoveAt(existing);
                }
                _entries.Add(entry);
                if (_entries.Count > MaximumEntries) _entries.RemoveAt(0);
                // Throttle repeated failures; a broken poller must not create a disk-write loop.
                if (!_dirty) { _dirty = true; _writer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan); }
                if (fatal) Flush();
                return entry.Id;
            }
        }
        catch { return null; } // Diagnostic failures must never replace the original application error.
    }

    public DiagnosticEntry[] Snapshot()
    {
        lock (_gate) { Prune(_clock()); return _entries.ToArray(); }
    }

    public void SetEnabled(bool enabled) { lock (_gate) _enabled = enabled; }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Reports.Invalidate();
            _dirty = true;
            FlushCore(); // Serialized with pending writes so cleared entries cannot reappear.
        }
    }

    public string ReportJson()
    {
        var entries = Snapshot();
        var json = JsonSerializer.Serialize(DiagnosticReports.Create(entries), Json);
        while (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes && entries.Length > 0)
        {
            entries = entries[1..];
            json = JsonSerializer.Serialize(DiagnosticReports.Create(entries), Json);
        }
        return json;
    }

    public async Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (string.Equals(fullPath, Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The diagnostic store cannot be used as an export destination.");
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = await Reports.GetAsync().WaitAsync(cancellationToken);
            await File.WriteAllTextAsync(temporary, report, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Flush()
    {
        try { lock (_gate) FlushCore(); }
        catch
        {
            PersistenceFailed = true;
            lock (_gate) { if (!_disposed) _writer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan); }
        }
    }

    private void FlushCore()
    {
        if (!_dirty) return;
        Prune(_clock());
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_entries, Json);
        while (bytes.Length > MaximumBytes && _entries.Count > 0)
        {
            _entries.RemoveAt(0);
            bytes = JsonSerializer.SerializeToUtf8Bytes(_entries, Json);
        }
        File.WriteAllBytes(_path + ".tmp", bytes);
        File.Move(_path + ".tmp", _path, true);
        PersistenceFailed = false;
        _dirty = false;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var file = File.OpenRead(_path);
            if (file.Length > MaximumBytes) return;
            var document = JsonNode.Parse(file);
            var entries = new List<DiagnosticEntry>();
            if (document is JsonArray records)
                foreach (var item in records)
                {
                    // 已停用或未知分区只丢弃自身，不影响仍可读取的记录。
                    try { if (item?.Deserialize<DiagnosticEntry>(Json) is { } entry) entries.Add(entry); }
                    catch (JsonException) { }
                }
            // Do not forward arbitrary edited log content through the report export.
            _entries.AddRange(entries.Where(Valid).TakeLast(MaximumEntries));
            Prune(_clock());
            if (_dirty) _writer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { PersistenceFailed = true; }
    }

    private void Prune(DateTimeOffset now)
    {
        if (_entries.RemoveAll(entry => now - entry.LastSeen > Retention || entry.LastSeen > now.AddMinutes(5)) > 0)
        {
            _dirty = true;
            if (!_disposed) _writer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
    }

    private static bool Valid(DiagnosticEntry? entry) => entry is not null
        && Hex(entry.Id, 12) && Hex(entry.Fingerprint, 16) && Enum.IsDefined(entry.Area)
        && Safe(entry.AppVersion, 160) && Safe(entry.Operation, 80)
        && (entry.PluginId is null || Safe(entry.PluginId, 80))
        && (entry.ClientVersion is null || DiagnosticReports.ClientVersion(entry.ClientVersion) == entry.ClientVersion) && entry.Count is > 0 and <= 1_000_000 && entry.FirstSeen <= entry.LastSeen
        && entry.Errors is { Length: <= 4 } && entry.Errors.All(error => error is not null
            && Safe(error.Type, 160) && error.Frames is { Length: <= 12 } && error.Frames.All(frame => frame is not null
                && Safe(frame.Method, 180) && (frame.Module == "" || Hex(frame.Module, 32))));
    private static bool Safe(string? value, int limit) => value is not null && value.Length <= limit && DiagnosticReports.Identifier(value, limit) == value;
    private static bool Hex(string? value, int length) => value?.Length == length && value.All(char.IsAsciiHexDigit);

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; Reports.Dispose(); _writer.Dispose(); Flush(); }
    }
}
