using System.Xml.Linq;
using WindowsIsland.Services;

internal static class SettingsChecks
{
    public static void Run(Action<bool, string> check)
    {
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "settings-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(output, "settings.json");
        var store = new SettingsStore(path);
        check(store.Current == new AppSettings(), "Fresh setup keeps all sources enabled and awaits completion");
        Directory.CreateDirectory(output);
        File.WriteAllText(path, "{\"SetupCompleted\":true,\"WeChat\":false}");
        var upgraded = new SettingsStore(path).Current;
        check(upgraded.Animations && upgraded.SetupCompleted && !upgraded.WeChat,
            "Existing preferences enable notification motion without resetting source choices");
        var saved = store.Current with { SetupCompleted = true, SystemNotifications = false, Telegram = false, Animations = false };
        var changes = 0;
        store.Changed += _ => changes++;
        store.Save(saved);
        check(new SettingsStore(path).Current == saved && changes == 1, "Setup completion and source preferences survive a restart");
        File.WriteAllText(path, "{invalid json");
        check(new SettingsStore(path).Current == new AppSettings(), "Damaged preferences return to an actionable first-run setup");
        var blocker = Path.Combine(output, "file");
        File.WriteAllText(blocker, "not a directory");
        var failing = new SettingsStore(Path.Combine(blocker, "settings.json"));
        try { failing.Save(saved); check(false, "Failed saves do not mark setup as complete"); }
        catch (IOException) { check(!failing.Current.SetupCompleted, "Failed saves do not mark setup as complete"); }

        var weChat = new IslandNotification(1, DateTimeOffset.Now, "微信", "小林", "你好", Source: NotificationSource.ClientHook, AppId: "wechat");
        var telegram = weChat with { AppName = "Telegram", AppId = "telegram" };
        var system = weChat with { Source = NotificationSource.SystemNotification, AppId = "Tencent.WeChat" };
        var power = weChat with { Source = NotificationSource.Power, AppName = "电源", AppId = "power" };
        check(saved.Allows(weChat) && !saved.Allows(system), "Local messages work when system notifications are disabled");
        check(!saved.Allows(telegram) && !(new AppSettings(WeChat: false)).Allows(system), "Source switches also filter matching system deliveries");
        check(!(new AppSettings(Power: false)).Allows(power) && (new AppSettings()).Allows(power), "Power notifications follow their saved switch");
        check(!saved.AllowsClient("telegram") && saved.AllowsClient("wechat") && !saved.AllowsClient("unknown"), "Disabled clients are excluded from hook discovery");

        var template = XDocument.Load(Path.Combine(Environment.CurrentDirectory, "packaging", "AppxManifest.xml"));
        var runtime = XDocument.Parse("""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Extensions>
                <Extension Category="windows.activatableClass.inProcessServer"><InProcessServer><Path>present.dll</Path></InProcessServer></Extension>
                <Extension Category="windows.activatableClass.inProcessServer"><InProcessServer><Path>missing.dll</Path></InProcessServer></Extension>
                <Extension Category="windows.activatableClass.proxyStub"><ProxyStub ClassId="fixture" /></Extension>
                <Extension Category="windows.unrelated" />
              </Extensions>
            </Package>
            """);
        var manifest = InitializationManifest.Build(template, runtime, file => file == "present.dll", new Version(1, 0, 0, 12));
        var ns = manifest.Root!.Name.Namespace;
        check((string?)manifest.Root.Element(ns + "Identity")?.Attribute("Version") == "1.0.0.13",
            "Re-initialization produces a package version newer than the registered version");
        check(manifest.Root.Element(ns + "Extensions")?.Elements().Count() == 2,
            "Initialization keeps runtime registrations only for available servers and supported categories");
        check(manifest.Descendants().Any(element => (string?)element.Attribute("Name") == "userNotificationListener")
            && (string?)template.Root!.Element(ns + "Identity")?.Attribute("Version") == "1.0.0.0",
            "Initialization preserves notification capability without mutating the supplied template");
    }
}
