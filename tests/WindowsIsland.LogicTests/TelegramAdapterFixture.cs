namespace WindowsIsland.Plugins;

// Generic backend lifecycle fixture, never registered by the application.
internal sealed class TelegramPlugin() : BuiltInMessageSource(BuiltInPluginIds.Telegram, "Telegram");
