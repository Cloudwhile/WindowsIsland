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
        check(upgraded.Animations && upgraded.SetupCompleted && !upgraded.WeChat && upgraded.Position == NotificationPosition.TopCenter,
            "Existing preferences enable notification motion without resetting source choices");
        check(!upgraded.IncludePrereleaseUpdates, "Existing settings stay on stable updates until the user opts in");
        check(upgraded.Language == "zh-CN", "Existing preferences keep the Chinese interface after upgrading");
        check(upgraded.MuteDuringGames, "Existing preferences enable automatic game mute without changing message sources");
        check(upgraded.MuteDuringFullScreen, "Existing preferences enable the independent full-screen switch");
        var saved = store.Current with { SetupCompleted = true, SystemNotifications = false, Telegram = false,
            Animations = false, Position = NotificationPosition.RightCenter, IncludePrereleaseUpdates = true, Language = "en-US", MuteDuringGames = false };
        var changes = 0;
        store.Changed += _ => changes++;
        store.Save(saved);
        check(new SettingsStore(path).Current == saved && changes == 1, "Setup completion and source preferences survive a restart");
        check(new SettingsStore(path).Current.IncludePrereleaseUpdates, "The prerelease update choice survives a restart");
        check(new SettingsStore(path).Current.Language == "en-US", "The selected language survives a restart");
        check(!new SettingsStore(path).Current.MuteDuringGames, "Disabling game mute survives a restart");
        check(new SettingsStore(path).Current.MuteDuringFullScreen,
            "Saving the game switch leaves the full-screen preference enabled");
        store.Save(saved with { Language = "system" });
        check(new SettingsStore(path).Current.Language == "system", "Following the system remains a preference instead of a fixed culture");
        store.Save(saved with { MuteDuringGames = true });
        check(new SettingsStore(path).Current.MuteDuringGames, "Enabling game mute survives a restart");
        foreach (var games in new[] { false, true })
        foreach (var fullScreen in new[] { false, true })
        {
            store.Save(saved with { MuteDuringGames = games, MuteDuringFullScreen = fullScreen });
            var restored = new SettingsStore(path).Current;
            check(restored.MuteDuringGames == games && restored.MuteDuringFullScreen == fullScreen
                && restored.Language == saved.Language && restored.WeChat == saved.WeChat,
                $"Both mute preferences persist independently: games={games}, fullScreen={fullScreen}");
        }
        foreach (var oldMute in new[] { false, true })
        {
            File.WriteAllText(path, "{\"SetupCompleted\":true,\"WeChat\":false,\"Language\":\"en-US\",\"MuteDuringGames\":"
                + oldMute.ToString().ToLowerInvariant() + "}");
            var migrated = new SettingsStore(path).Current;
            check(migrated.MuteDuringGames == oldMute && migrated.MuteDuringFullScreen == oldMute
                && migrated.SetupCompleted && !migrated.WeChat && migrated.Language == "en-US",
                "The old combined mute preference migrates to both switches: " + oldMute);
        }
        foreach (var gameMute in new[] { "999", "\"false\"", "null", "[]", "{}" })
        {
            File.WriteAllText(path, "{\"SetupCompleted\":true,\"WeChat\":false,\"Language\":\"en-US\",\"IncludePrereleaseUpdates\":true,\"MuteDuringGames\":" + gameMute + "}");
            var invalidGameMute = new SettingsStore(path).Current;
            check(invalidGameMute.MuteDuringGames && invalidGameMute.SetupCompleted && !invalidGameMute.WeChat
                && invalidGameMute.Language == "en-US" && invalidGameMute.IncludePrereleaseUpdates,
                "Invalid game mute values keep other valid preferences: " + gameMute);
            File.WriteAllText(path, "{\"SetupCompleted\":true,\"WeChat\":false,\"Language\":\"en-US\",\"MuteDuringGames\":false,\"MuteDuringFullScreen\":" + gameMute + "}");
            var invalidFullScreen = new SettingsStore(path).Current;
            check(!invalidFullScreen.MuteDuringGames && invalidFullScreen.MuteDuringFullScreen
                && invalidFullScreen.SetupCompleted && !invalidFullScreen.WeChat && invalidFullScreen.Language == "en-US",
                "Invalid full-screen mute values preserve the disabled game preference: " + gameMute);
            File.WriteAllText(path, "{\"SetupCompleted\":true,\"MuteDuringGames\":" + gameMute + ",\"MuteDuringFullScreen\":false}");
            var invalidGameOnly = new SettingsStore(path).Current;
            check(invalidGameOnly.MuteDuringGames && !invalidGameOnly.MuteDuringFullScreen && invalidGameOnly.SetupCompleted,
                "Invalid game mute values preserve the disabled full-screen preference: " + gameMute);
        }
        foreach (var language in new[] { "999", "true", "null", "[]", "{}", "\"fr-FR\"" })
        {
            File.WriteAllText(path, "{\"SetupCompleted\":true,\"WeChat\":false,\"IncludePrereleaseUpdates\":true,\"Language\":" + language + "}");
            var invalidLanguage = new SettingsStore(path).Current;
            check(invalidLanguage.Language == "zh-CN" && invalidLanguage.SetupCompleted && !invalidLanguage.WeChat
                && invalidLanguage.IncludePrereleaseUpdates, "Invalid language values preserve the other saved choices: " + language);
        }
        File.WriteAllText(path, "{\"SetupCompleted\":true,\"WeChat\":false,\"Animations\":false,\"IncludePrereleaseUpdates\":\"unknown\"}");
        var invalidChannel = new SettingsStore(path).Current;
        check(invalidChannel.SetupCompleted && !invalidChannel.WeChat && !invalidChannel.Animations && !invalidChannel.IncludePrereleaseUpdates,
            "An invalid update channel falls back to stable releases without resetting valid preferences");
        File.WriteAllText(path, "{\"SetupCompleted\":true,\"WeChat\":false,\"Position\":999}");
        var invalidPosition = new SettingsStore(path).Current;
        check(invalidPosition.Position == NotificationPosition.TopCenter && invalidPosition.SetupCompleted && !invalidPosition.WeChat,
            "An unknown position falls back to top center without resetting other preferences");
        PositionChecks.Run(check);
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
