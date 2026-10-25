using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal enum PinnedChangeKind { Added, Updated, Removed }
internal sealed record PinnedContentChange(PinnedChangeKind Kind, string PluginId, string Id, PinnedContent? Content = null);

internal sealed partial class PluginManager
{
    public event Action<PinnedContentChange>? PinnedChanged;

    private void DispatchPinned(Entry entry, IMessageSource plugin, PinnedContentChange change) => Dispatch(entry, plugin, () =>
    {
        if (!entry.Registration.Definition.IsEnabled(_settings)
            || entry.Trust is { } trust && !trust.IsGranted(PluginPermissions.Notifications)) return;
        if (change.Kind == PinnedChangeKind.Removed)
        {
            if (entry.Pinned?.Id != change.Id) return;
            entry.Pinned = null;
        }
        else
        {
            var content = change.Content ?? throw new InvalidDataException("Missing pinned content.");
            var metadata = entry.Registration.Definition.Metadata;
            content = PinnedContentPolicy.Normalize(content, plugin.Id,
                metadata?.IsBuiltIn == true && metadata.External is null,
                () => Diagnostic(entry).Capture(new InvalidOperationException(), "RejectPinnedXaml"));
            change = change with { Content = content };
            if (change.Kind == PinnedChangeKind.Updated && entry.Pinned?.Id != content.Id) return;
            entry.Pinned = content;
        }
        // StartAsync 可以提交首个内容，只有启动成功后才对宿主发布。
        if (entry.Active) PinnedChanged?.Invoke(change);
    });

    private void ClearPinned(Entry entry)
    {
        var pinned = entry.Pinned;
        entry.Pinned = null;
        if (pinned is null) return;
        try { PinnedChanged?.Invoke(new(PinnedChangeKind.Removed, pinned.PluginId, pinned.Id)); }
        catch (Exception error) { Diagnostic(entry).Capture(error, "RemovePinned"); }
    }

    public async Task InteractPinnedAsync(PinnedContent content, PinnedControlInteraction interaction)
    {
        if (_disposed) return;
        var entry = Entries.FirstOrDefault(item => item.Registration.Definition.Id == content.PluginId);
        if (entry is null) return;
        var plugin = entry.Plugin;
        var context = entry.Context;
        await entry.Interactions.WaitAsync();
        try
        {
            if (_disposed || !entry.Active || !ReferenceEquals(entry.Plugin, plugin) || context is null
                || entry.Pinned is not { } current || current.Id != content.Id || !PinnedContentPolicy.AllowsInteraction(current, interaction)) return;
            await Task.Run(() => context.HandleInteractionAsync(interaction));
        }
        catch (Exception error) { if (ReferenceEquals(entry.Plugin, plugin)) Fail(entry, error, "PinnedInteraction"); }
        finally { entry.Interactions.Release(); }
    }

}
