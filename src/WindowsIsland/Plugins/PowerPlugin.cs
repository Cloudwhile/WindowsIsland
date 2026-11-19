using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal interface IPowerQuotaBackend
{
    void ConfigureQuota(PowerQuotaSettings quota);
}

internal sealed class PowerPlugin() : BuiltInMessageSource(BuiltInPluginIds.Power, "Power"), IPluginSettingsChanged
{
    protected override void OnBackendStarting(IMessageSourceBackend backend)
    {
        if (backend is IPowerQuotaBackend quota)
            quota.ConfigureQuota(PowerQuotaSettings.From(Context?.Settings));
    }

    public Task OnSettingsChangedAsync(IReadOnlyList<string> keys)
    {
        if (Context is { } context && Backend is IPowerQuotaBackend quota)
            quota.ConfigureQuota(PowerQuotaSettings.From(context.Settings));
        return Task.CompletedTask;
    }
}
