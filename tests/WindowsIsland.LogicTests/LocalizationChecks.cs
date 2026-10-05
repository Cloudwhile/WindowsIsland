using System.Text;
using System.Xml.Linq;
using WindowsIsland.Services;

internal static class LocalizationChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(LanguagePreferences.Normalize(null) == "zh-CN" && LanguagePreferences.Normalize("unknown") == "zh-CN"
            && LanguagePreferences.Normalize(" EN-us ") == "en-US", "Language preferences normalize and unknown values keep Chinese fallback");
        check(LanguagePreferences.Resolve("system", ["en-GB"]) == "en-US"
            && LanguagePreferences.Resolve("system", ["zh-TW"]) == "zh-CN"
            && LanguagePreferences.Resolve("system", ["fr-FR"]) == "zh-CN"
            && LanguagePreferences.Resolve("system", ["fr-FR", "en-AU"]) == "en-US"
            && LanguagePreferences.Resolve("zh-CN", ["en-US"]) == "zh-CN",
            "System language matching follows preference order and explicit choices override it");
        var folder = Path.Combine(Environment.CurrentDirectory, "src", "WindowsIsland", "Strings");
        var chinese = Read(Path.Combine(folder, "Resources.resx"));
        var english = Read(Path.Combine(folder, "Resources.en-US.resx"));
        check(chinese.Keys.Order().SequenceEqual(english.Keys.Order()), "Supported languages contain the same resource keys");
        check(chinese.All(item => CompositeFormat.Parse(item.Value).MinimumArgumentCount
            == CompositeFormat.Parse(english[item.Key]).MinimumArgumentCount), "Translations preserve their formatting arguments");
        var changes = 0;
        void Changed() => changes++;
        Localization.Configure("zh-CN");
        Localization.Changed += Changed;
        try
        {
            Localization.Configure("en-US");
            check(english.All(item => Localization.Get(item.Key) == item.Value), "English translations load from the built satellite assembly");
            check(Localization.Format("UpdateFoundPrerelease", "0.1.1-rc.1").Contains("0.1.1-rc.1", StringComparison.Ordinal)
                && Localization.Format("PowerBattery", 73) == "Battery 73%", "Versions and battery percentages format in the selected language");
            var error = Localization.DataError("UpdateErrorChecksumMismatch");
            Localization.Configure("zh-CN");
            check(chinese.All(item => Localization.Get(item.Key) == item.Value), "Chinese translations load from the default embedded resources");
            check(Localization.Get(Localization.ErrorKey(error, "UpdateFailed")) == "更新包校验失败，请重新下载。",
                "Errors keep their resource identity when the display language changes");
            Localization.Configure("system", ["zh-HK"]);
            check(changes == 2, "Choosing a language with the same resolved culture does not emit redundant refreshes");
            var router = new MessageRouter();
            var message = new IslandNotification(1, DateTimeOffset.Now, "WeChat", "小林", "原文 stays unchanged",
                Source: NotificationSource.SystemNotification, AppId: "wechat");
            check(router.Receive(message)?.Notification == message, "A message enters the router unchanged");
            Localization.Configure("en-US");
            check(router.Receive(message with { Source = NotificationSource.ClientHook, EventId = "i18n/message" }) is null
                && !router.HasPending && message.Title == "小林" && message.Body == "原文 stays unchanged",
                "Changing language does not alter message text or its deduplication identity");
            var power = new PowerTracker();
            power.Update(new(true, false, false, 73));
            var charging = power.Update(new(true, true, true, 73));
            check(charging?.Title == "Charging started" && charging.Body == "Battery 73%" && charging.AppId == "power",
                "Power notifications translate while retaining their stable source identity");
        }
        finally { Localization.Changed -= Changed; Localization.Configure("zh-CN"); }
    }

    private static Dictionary<string, string> Read(string path) => XDocument.Load(path).Root!.Elements("data")
        .ToDictionary(item => (string)item.Attribute("name")!, item => item.Element("value")!.Value);
}
