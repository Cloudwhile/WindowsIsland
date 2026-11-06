using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

/// <summary>插件标识和文件位置的可选择值与显式复制操作。</summary>
internal sealed class PluginCopyField : Grid
{
    public TextBlock Value { get; }

    public PluginCopyField(string label, string? value, string automationId, string copyLabel)
    {
        ColumnSpacing = SettingsTokens.RowPadding;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var field = new SettingsField(label, value, automationId);
        Value = field.Value;
        Children.Add(field);
        var copy = new IconActionButton(Symbol.Copy, copyLabel, automationId + ".Copy")
        {
            VerticalAlignment = VerticalAlignment.Top,
            IsEnabled = !string.IsNullOrWhiteSpace(value)
        };
        Grid.SetColumn(copy, 1);
        Children.Add(copy);
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new DataPackage();
                data.SetText(Value.Text);
                Clipboard.SetContent(data);
            }
            catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Settings, error, "SettingsSave"); }
        };
    }
}
