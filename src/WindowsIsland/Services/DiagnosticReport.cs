using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WindowsIsland.Services;

internal enum DiagnosticArea { Application, Settings, Notifications, WeChat, Telegram, Updates, WindowsSettings, Diagnostics }

internal sealed record DiagnosticFrame(string Method, int Token, int Offset, string Module);
internal sealed record DiagnosticError(string Type, int HResult, DiagnosticFrame[] Frames);
internal sealed record DiagnosticEntry(string Id, string Fingerprint, string AppVersion, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    DiagnosticArea Area, string Operation, bool Fatal, int Count, DiagnosticError[] Errors, string? ClientVersion = null);
internal sealed record DiagnosticReport(int SchemaVersion, string AppVersion, string OperatingSystem, string Architecture,
    string Runtime, DateTimeOffset ExportedAt, DiagnosticEntry[] Events);

internal static class DiagnosticReports
{
    public static string AppVersion => ReleaseVersion.Current.Tag;

    public static DiagnosticReport Create(DiagnosticEntry[] entries) => new(1, AppVersion,
        Environment.OSVersion.Version.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.Version.ToString(), DateTimeOffset.UtcNow, entries);

    public static DiagnosticEntry Capture(DiagnosticArea area, string operation, Exception? error, bool fatal, DateTimeOffset now,
        string? clientVersion = null)
    {
        var errors = new List<DiagnosticError>();
        var pending = new Queue<Exception>();
        if (error is not null) pending.Enqueue(error);
        while (pending.TryDequeue(out var current) && errors.Count < 4)
        {
            // Exception messages, Data, Source and raw stack strings can contain user content.
            // Keep only code metadata; neither file paths nor argument values are inspected.
            var frames = new StackTrace(current, false).GetFrames().Take(12).Select(frame =>
            {
                var method = frame.GetMethod();
                return new DiagnosticFrame(Identifier(method?.DeclaringType?.FullName + "." + method?.Name, 180),
                    method is null || method.Module.Assembly.IsDynamic ? 0 : method.MetadataToken,
                    frame.GetILOffset(), method?.Module.ModuleVersionId.ToString("N") ?? "");
            }).ToArray();
            errors.Add(new(Identifier(current.GetType().FullName, 160), current.HResult, frames));
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.Take(4)) pending.Enqueue(inner);
            else if (current.InnerException is { } inner) pending.Enqueue(inner);
        }
        operation = Identifier(operation, 80);
        var identity = $"{AppVersion}/{area}/{operation}/{fatal}/" + string.Join('/', errors.Select(item =>
            item.Type + ":" + item.HResult + ":" + string.Join(',', item.Frames.Select(frame => frame.Method))));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return new(Guid.NewGuid().ToString("N")[..12], fingerprint, AppVersion, now, now, area, operation, fatal, 1, errors.ToArray(),
            ClientVersion(clientVersion));
    }

    // Third-party client versions are reduced to their numeric form; the merge identity ignores them.
    internal static string? ClientVersion(string? value)
    {
        var version = new string((value ?? "").TakeWhile(character => char.IsAsciiDigit(character) || character == '.').Take(32).ToArray());
        return version.Length == 0 ? null : version;
    }

    internal static string Identifier(string? value, int maximum) => string.Concat((value ?? "unknown").Take(maximum)
        .Select(character => char.IsAsciiLetterOrDigit(character) || "._+`<>-".Contains(character) ? character : '_'));

    public static Uri FeedbackUri() => new("https://github.com/Cloudwhile/WindowsIsland/issues/new?template=bug_report.yml");
}
