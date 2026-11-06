using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal static class PinnedContentPolicy
{
    public static PinnedContent Normalize(PinnedContent content, string pluginId, bool reviewed, Action rejectedXaml)
    {
        if (content.PluginId != pluginId || string.IsNullOrWhiteSpace(content.Id) || content.Id.Length > 128
            || content.Title is null || content.Title.Length > 256 || content.CompactText is null || content.CompactText.Length > 256
            || content.Body?.Length > 8192 || content.ActionId?.Length > 128 || content.Symbol?.Length > 64)
            throw new InvalidDataException("Invalid pinned content.");
        if (content.CustomPanelXaml is not null && !reviewed)
        {
            rejectedXaml();
            content = content with { CustomPanelXaml = null };
        }
        if (content.CustomPanelXaml?.Length > 32768 || content.Controls?.Count > 32)
            throw new InvalidDataException("Pinned panel is too large.");
        var controls = content.Controls?.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in controls ?? [])
        {
            if (control is null || string.IsNullOrWhiteSpace(control.Id) || control.Id.Length > 128 || !ids.Add(control.Id)
                || control.Label is null || control.Label.Length > 256 || !Enum.IsDefined(control.Kind)
                || control.Symbol?.Length > 64 || control.Kind == PinnedControlKind.Slider &&
                (!double.IsFinite(control.Minimum) || !double.IsFinite(control.Maximum) || !double.IsFinite(control.Value)
                 || !double.IsFinite(control.Step) || control.Maximum <= control.Minimum || control.Step <= 0
                 || !double.IsFinite(control.Maximum - control.Minimum) || !double.IsFinite((control.Maximum - control.Minimum) / control.Step)
                 || control.Value < control.Minimum || control.Value > control.Maximum))
                throw new InvalidDataException("Invalid pinned control.");
        }
        return content with { Controls = controls is null ? null : Array.AsReadOnly(controls) };
    }

    public static bool AllowsInteraction(PinnedContent content, PinnedControlInteraction interaction)
    {
        var control = content.Controls?.FirstOrDefault(c => c.Id == interaction.ControlId);
        if (content.Id != interaction.ContentId || control is not { IsEnabled: true }) return false;
        return control.Kind switch
        {
            PinnedControlKind.Button => interaction.IsOn is null && interaction.Value is null,
            PinnedControlKind.Toggle => interaction.IsOn is not null && interaction.Value is null,
            PinnedControlKind.Slider => interaction.IsOn is null && interaction.Value is { } value && double.IsFinite(value)
                && value >= control.Minimum && value <= control.Maximum
                && (value == control.Maximum || Math.Abs((value - control.Minimum) / control.Step - Math.Round((value - control.Minimum) / control.Step)) < 0.000001),
            _ => false
        };
    }
}
