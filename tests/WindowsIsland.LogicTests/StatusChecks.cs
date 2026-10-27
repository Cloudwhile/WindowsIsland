using WindowsIsland.Services;
using WindowsIsland.Plugins;

internal static class StatusChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(SourceStatus.Tone("Connected") == StatusTone.Success && SourceStatus.Tone("WeChatUnsupported") == StatusTone.Critical
            && SourceStatus.Tone("NotRunning") == StatusTone.Neutral && SourceStatus.Tone("Off") == StatusTone.Neutral,
            "Source states map to success, failure and neutral tones");
        check(SourceStatus.DetailKey(BuiltInPluginIds.WeChat, "WeChatUnsupported") == "WeChatUnsupportedDetail"
            && SourceStatus.DetailKey(BuiltInPluginIds.Telegram, "TelegramNotDetected") == "TelegramNotDetectedDetail"
            && SourceStatus.DetailKey(BuiltInPluginIds.WeChat, "Connected") is null, "Only failed or missing sources show guidance");
        check(SourceStatus.DetailKey(BuiltInPluginIds.WeChat, "WeChatUnsupported", false) == "WeChatUnsupportedNoFallbackDetail"
            && SourceStatus.DetailKey(BuiltInPluginIds.WeChat, "WeChatConnectFailed", false) == "WeChatConnectFailedNoFallbackDetail",
            "WeChat guidance does not claim a system fallback when system notifications are unavailable");
        check(SourceStatus.DetailKey(BuiltInPluginIds.Power, "Unavailable") == "SourceUnavailableDetail"
            && SourceStatus.DetailKey(BuiltInPluginIds.System, "Denied") == "SourceDeniedDetail",
            "Unavailable sources and denied access offer guidance below their result state");

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
            check(DiagnosticPresentation.Title(error) == "读取消息失败" && DiagnosticPresentation.Reason(error) == "System.InvalidOperationException（0x80131509）",
                "Message read failures show a translated event title and structured error code");
            check(DiagnosticPresentation.Title(Entry("FutureOperation")) == "微信 · 操作失败",
                "Unknown diagnostic operations retain a readable localized fallback");
            check(DiagnosticPresentation.Title(Entry("Start")) == "插件启动失败"
                && DiagnosticPresentation.Title(Entry("RejectPinnedXaml")) == "固定内容面板未加载",
                "Plugin lifecycle and pinned panel events have translated titles");
            check(DiagnosticPresentation.Area(Entry("Start") with { Area = DiagnosticArea.PowerPlugin }) == "系统通知"
                && DiagnosticPresentation.Area(Entry("Start") with { Area = DiagnosticArea.Application, PluginId = "wini_plugin_fixture" }) == "插件",
                "Built-in and external plugin diagnostic areas have readable names");
            check(DiagnosticPresentation.Outcome(enabled with { Errors = error.Errors }) == DiagnosticOutcome.Failure
                && DiagnosticPresentation.Outcome(enabled with { Fatal = true }) == DiagnosticOutcome.Failure
                && DiagnosticPresentation.Title(enabled with { Errors = error.Errors }) == "无障碍补丁启用失败",
                "Error metadata or fatal status overrides a success-like event name and title");
            check(DiagnosticPresentation.Suggestion(patch)?.Contains("消息来源", StringComparison.Ordinal) == true,
                "Retry guidance points to the actual location of the source controls");
            check(Localization.Get("TrayConnecting") == Localization.Get("NotConnected")
                && Localization.Get("TrayListening") == Localization.Get("Connected"),
                "Tray state wording matches the result states used by settings");
            Localization.Configure("en-US");
            check(DiagnosticPresentation.Summary(patch) == "Failed · 19 times", "Diagnostic summaries follow the selected language");
            check(DiagnosticPresentation.Title(error) == "Could not read messages"
                && DiagnosticPresentation.Title(Entry("FutureOperation")) == "WeChat · Operation failed"
                && DiagnosticPresentation.Area(Entry("Start") with { Area = DiagnosticArea.PowerPlugin }) == "System notifications",
                "Diagnostic event names and fallbacks follow the selected language");
            check(DiagnosticPresentation.Suggestion(patch)?.Contains("Message sources", StringComparison.Ordinal) == true,
                "English retry guidance follows the relocated source controls");
        }
        finally { Localization.Configure("zh-CN"); }
        check(DiagnosticReports.ClientVersion("4.0.1.12 (x64)\\Users") == "4.0.1.12" && DiagnosticReports.ClientVersion("C:\\x") is null,
            "Client versions keep only their numeric form");
    }
}
