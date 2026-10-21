using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

/// <summary>设置详情中可换行、可复制的标签和值。</summary>
internal sealed class SettingsField : StackPanel
{
    public TextBlock Value { get; }

    public SettingsField(string title, Control editor) : this(null, "", placeholder: false)
    {
        Children.Clear();
        var label = IslandTheme.Text(title, 12, secondary: true);
        label.TextWrapping = TextWrapping.Wrap;
        Children.Add(label);
        AutomationProperties.SetName(editor, title);
        Children.Add(editor);
    }

    public SettingsField(string? label, string? value, string? automationId = null, bool placeholder = true)
    {
        Spacing = 4;
        if (label is not null) Children.Add(LocalizedUI.Text(label, 12, secondary: true));
        Value = IslandTheme.Text(value ?? "", label is null ? 12 : 14, secondary: label is null);
        Value.TextWrapping = TextWrapping.Wrap;
        Value.IsTextSelectionEnabled = true;
        if (automationId is not null) AutomationProperties.SetAutomationId(Value, automationId);
        if (placeholder && string.IsNullOrWhiteSpace(value))
            LocalizedUI.Bind(Value, () => Value.Text = Localization.Get("PluginUnknown"));
        Children.Add(Value);
    }
}
