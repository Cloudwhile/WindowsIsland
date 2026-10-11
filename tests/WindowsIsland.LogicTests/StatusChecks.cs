using WindowsIsland.Services;

internal static class StatusChecks
{
    public static void Run(Action<bool, string> check)
    {
        var health = new WeChatConnectionHealth();
        check(health.StatusKey(false, 0) == "NotRunning" && health.StatusKey(false, 1) == "NotConnected",
            "WeChat reports not running without a client and not connected before any failure threshold");
        health.Failed(7, unsupported: true);
        health.Failed(7, unsupported: true);
        check(health.StatusKey(false, 1) == "NotConnected", "Isolated accessibility failures do not degrade the WeChat state");
        health.Failed(7, unsupported: true);
        check(health.StatusKey(false, 1) == "WeChatUnsupported", "Consecutive unsupported failures degrade WeChat to version not supported");
        check(health.StatusKey(true, 1) == "Connected", "A readable WeChat client reports connected even after earlier failures");
        health.Retry();
        check(health.StatusKey(false, 1) == "NotConnected", "Retry clears the failure state for one more attempt");
        health.Failed(7, unsupported: true);
        check(health.StatusKey(false, 1) == "WeChatUnsupported", "A failed retry degrades again immediately");
        health.Succeeded(7);
        health.Failed(8, unsupported: false);
        health.Failed(8, unsupported: false);
        health.Failed(8, unsupported: false);
        check(health.StatusKey(false, 2) == "WeChatConnectFailed", "Access failures are reported separately from unsupported versions");
        health.Remove(8);
        check(health.StatusKey(false, 1) == "NotConnected", "A closed client discards its failure history");
        check(WeChatConnectionHealth.IsUnsupported("AccessorMismatch") && !WeChatConnectionHealth.IsUnsupported("OpenProcess"),
            "Signature mismatches are version incompatibilities while process access is not");

        check(SourceStatus.Tone("Connected") == StatusTone.Success && SourceStatus.Tone("WeChatUnsupported") == StatusTone.Critical
            && SourceStatus.Tone("NotRunning") == StatusTone.Neutral && SourceStatus.Tone("Off") == StatusTone.Neutral,
            "Source states map to success, failure and neutral tones");
        check(SourceStatus.DetailKey("wechat", "WeChatUnsupported") == "WeChatUnsupportedDetail"
            && SourceStatus.DetailKey("telegram", "TelegramNotDetected") == "TelegramNotDetectedDetail"
            && SourceStatus.DetailKey("wechat", "Connected") is null, "Only failed or missing sources show guidance");

        var now = DateTimeOffset.UtcNow;
        DiagnosticEntry Entry(string operation, DiagnosticError[]? errors = null) => new("0123456789ab", "0123456789abcdef",
            DiagnosticReports.AppVersion, now, now, DiagnosticArea.WeChat, operation, false, 19, errors ?? [], "4.0.1.12");
        Localization.Configure("zh-CN");
        try
        {
            var patch = Entry("Accessibility.AccessorMismatch");
            check(DiagnosticPresentation.Title(patch) == "无障碍补丁启用失败" && DiagnosticPresentation.Summary(patch) == "失败 · 19 次"
                && DiagnosticPresentation.ShortId(patch) == "6789ab", "Diagnostic entries show a readable title, failed count and short ID");
            check(DiagnosticPresentation.Reason(patch) == "当前微信版本暂不支持直连" && DiagnosticPresentation.CanRetrySource(patch),
                "Accessibility failures explain the version problem and offer a retry");
            var enabled = Entry("AccessibilityEnabled");
            check(DiagnosticPresentation.Outcome(enabled) == DiagnosticOutcome.Success && DiagnosticPresentation.Summary(enabled) == "成功 · 19 次"
                && DiagnosticPresentation.Reason(enabled) is null && !DiagnosticPresentation.CanRetrySource(enabled),
                "Successful state records are not shown as failures");
            var error = Entry("ReadLoop", [new("System.InvalidOperationException", unchecked((int)0x80131509), [])]);
            check(DiagnosticPresentation.Title(error) == "微信 · ReadLoop" && DiagnosticPresentation.Reason(error) == "System.InvalidOperationException（0x80131509）",
                "Unmapped failures fall back to their component, operation and error code");
            Localization.Configure("en-US");
            check(DiagnosticPresentation.Summary(patch) == "Failed · 19 times", "Diagnostic summaries follow the selected language");
        }
        finally { Localization.Configure("zh-CN"); }
        check(DiagnosticReports.ClientVersion("4.0.1.12 (x64)\\Users") == "4.0.1.12" && DiagnosticReports.ClientVersion("C:\\x") is null,
            "Client versions keep only their numeric form");
    }
}
