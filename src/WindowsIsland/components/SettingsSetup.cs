using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

internal sealed class SettingsSetup : Grid
{
    private readonly SettingsExpander _expander = new(new SymbolIcon(Symbol.Permissions), "Setup", "InitializationSection", "InitializationStatus");
    public string Status { get => _expander.Header.Status; set => _expander.Header.Status = value; }
    public Services.StatusTone Tone { get => _expander.Header.Tone; set => _expander.Header.Tone = value; }
    public bool IsExpanded { get => _expander.IsExpanded; set => _expander.IsExpanded = value; }

    public SettingsSetup(params UIElement[] rows)
    {
        foreach (var row in rows) _expander.Rows.Add(row);
        Children.Add(_expander);
    }
}
