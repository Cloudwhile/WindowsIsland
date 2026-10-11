namespace WindowsIsland.Services;

internal enum StatusTone { Neutral, Success, Critical }
internal enum DiagnosticOutcome { Failure, Success }

// Settings present results, not progress. Keys are resource keys shared by every page.
internal static class SourceStatus
{
    public static StatusTone Tone(string key) => key switch
    {
        "Connected" or "On" => StatusTone.Success,
        "WeChatUnsupported" or "WeChatConnectFailed" or "Unavailable" or "Denied" => StatusTone.Critical,
        _ => StatusTone.Neutral
    };

    public static bool IsRunning(string key) => Tone(key) == StatusTone.Success;

    // Secondary guidance shown below a source; null when the status needs no explanation.
    public static string? DetailKey(string id, string key) => (id, key) switch
    {
        ("wechat", "WeChatUnsupported") => "WeChatUnsupportedDetail",
        ("wechat", "WeChatConnectFailed") => "WeChatConnectFailedDetail",
        ("telegram", "TelegramNotDetected") => "TelegramNotDetectedDetail",
        _ => null
    };
}

internal static class DiagnosticPresentation
{
    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["AccessibilityEnabled"] = "DiagnosticTitleAccessibilityEnabled",
        ["AccessibilityRestored"] = "DiagnosticTitleAccessibilityRestored",
        ["AccessibilityUnavailable"] = "DiagnosticTitleAccessibilityFailed",
        ["AccessibilityLost"] = "DiagnosticTitleAccessibilityLost",
        ["UpdateInstallFailed"] = "DiagnosticTitleUpdateFailed",
        ["UpdateRestored"] = "DiagnosticTitleUpdateFailed",
        ["UpdateBackupRetained"] = "DiagnosticTitleUpdateFailed",
        ["Startup"] = "DiagnosticTitleStartup",
        ["Unhandled"] = "DiagnosticTitleUnhandled",
        ["UserInterface"] = "DiagnosticTitleUnhandled",
        ["BackgroundTask"] = "DiagnosticTitleUnhandled",
        ["ReadPreferences"] = "DiagnosticTitleReadPreferences",
        ["ReadBanners"] = "DiagnosticTitleReadWindowsSettings",
        ["ReadDoNotDisturb"] = "DiagnosticTitleReadWindowsSettings",
        ["ReadStartup"] = "DiagnosticTitleReadWindowsSettings"
    };

    public static DiagnosticOutcome Outcome(DiagnosticEntry entry) => entry.Errors.Length == 0 && !entry.Fatal
        && entry.Operation is "AccessibilityEnabled" or "AccessibilityRestored" ? DiagnosticOutcome.Success : DiagnosticOutcome.Failure;

    public static string Title(DiagnosticEntry entry)
    {
        if (entry.Operation.StartsWith("Accessibility.", StringComparison.Ordinal)) return Localization.Get("DiagnosticTitleAccessibilityFailed");
        if (Titles.TryGetValue(entry.Operation, out var key)) return Localization.Get(key);
        return Localization.Format("DiagnosticTitleFallback", Localization.Get("DiagnosticsArea" + entry.Area), entry.Operation);
    }

    public static string Summary(DiagnosticEntry entry) => Localization.Format(
        Outcome(entry) == DiagnosticOutcome.Success ? "DiagnosticSucceededCount" : "DiagnosticFailedCount", entry.Count);

    // Only the tail is shown; the full random identifier remains in the exported report.
    public static string ShortId(DiagnosticEntry entry) => entry.Id.Length <= 6 ? entry.Id : entry.Id[^6..];

    public static string? Reason(DiagnosticEntry entry)
    {
        if (Outcome(entry) == DiagnosticOutcome.Success) return null;
        if (entry.Operation.StartsWith("Accessibility.", StringComparison.Ordinal))
            return Localization.Get(WeChatConnectionHealth.IsUnsupported(entry.Operation["Accessibility.".Length..])
                ? "DiagnosticReasonUnsupported" : "DiagnosticReasonProcessAccess");
        if (entry.Operation is "AccessibilityUnavailable") return Localization.Get("DiagnosticReasonNoAutomation");
        if (entry.Operation is "AccessibilityLost") return Localization.Get("DiagnosticReasonAccessibilityLost");
        return entry.Errors.FirstOrDefault() is { } error
            ? Localization.Format("DiagnosticReasonError", error.Type, "0x" + error.HResult.ToString("X8"))
            : Localization.Get("DiagnosticReasonUnknown");
    }

    public static bool CanRetrySource(DiagnosticEntry entry) => Outcome(entry) == DiagnosticOutcome.Failure
        && entry.Area == DiagnosticArea.WeChat && entry.Operation.StartsWith("Accessibility", StringComparison.Ordinal);

    public static string? Suggestion(DiagnosticEntry entry) => Outcome(entry) == DiagnosticOutcome.Success ? null
        : Localization.Get(CanRetrySource(entry) ? "DiagnosticSuggestionRetry" : "DiagnosticSuggestionReport");
}
