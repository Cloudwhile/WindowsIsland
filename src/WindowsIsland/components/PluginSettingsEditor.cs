using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

/// <summary>使用宿主设置通道编辑插件声明的字符串和布尔值，与启停状态无关。</summary>
internal sealed class PluginSettingsEditor : SettingsContent
{
    public PluginSettingsEditor(string id, PluginSettingsModel model) : base(false)
    {
        foreach (var field in model.Fields)
        {
            Control editor;
            var syncing = false;
            if (field.Type == PluginSettingType.Boolean)
            {
                var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0,
                    IsOn = model.Settings.GetBoolean(field.Key, (bool)field.DefaultValue!) };
                toggle.Toggled += (_, _) =>
                {
                    if (syncing) return;
                    try { model.Settings.SetBoolean(field.Key, toggle.IsOn); }
                    catch (Exception error)
                    {
                        syncing = true;
                        toggle.IsOn = model.Settings.GetBoolean(field.Key, (bool)field.DefaultValue!);
                        syncing = false;
                        Failed(error);
                    }
                };
                editor = toggle;
            }
            else
            {
                var text = new TextBox { Text = model.Settings.GetString(field.Key, (string?)field.DefaultValue) ?? "",
                    TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
                text.TextChanged += (_, _) =>
                {
                    if (syncing) return;
                    try { model.Settings.SetString(field.Key, text.Text); }
                    catch (Exception error) { Failed(error); }
                };
                editor = text;
            }
            AutomationProperties.SetAutomationId(editor, "PluginSetting." + id + "." + field.Key);
            Children.Add(new SettingsField(field.Title, editor));
        }
        void Failed(Exception error)
        {
            new PluginDiagnostics(id, DiagnosticArea.Application).Capture(error, "SettingsSave");
            _error.Visibility = Visibility.Visible;
        }
        Children.Add(SettingsContent.Wrap(_error));
    }

    private readonly TextBlock _error = CreateError();
    private static TextBlock CreateError()
    {
        var error = LocalizedUI.Text("ErrorSettingsSave", 12, secondary: true);
        error.TextWrapping = TextWrapping.Wrap;
        error.Visibility = Visibility.Collapsed;
        return error;
    }
}
