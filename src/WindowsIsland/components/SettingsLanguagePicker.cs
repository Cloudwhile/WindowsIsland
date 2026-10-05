using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsLanguagePicker : Grid
{
    private readonly ComboBox _picker = new() { MinWidth = 0, MaxWidth = 168 };
    private readonly Dictionary<string, ComboBoxItem> _items = [];
    private bool _syncing;
    public event Action<string>? LanguageSelected;
    public Control Input => _picker;

    public SettingsLanguagePicker()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        Children.Add(_picker);
        foreach (var (code, label) in new[] { (LanguagePreferences.System, ""), ("zh-CN", "简体中文"), ("en-US", "English") })
        {
            var item = new ComboBoxItem { Tag = code, Content = label };
            _items.Add(code, item);
            _picker.Items.Add(item);
        }
        LocalizedUI.Bind(this, () => _items[LanguagePreferences.System].Content = Localization.Get("LanguageSystem"));
        LocalizedUI.Label(_picker, "Language");
        AutomationProperties.SetAutomationId(_picker, "LanguagePicker");
        _picker.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _picker.SelectedItem is ComboBoxItem { Tag: string code }) LanguageSelected?.Invoke(code);
        };
    }

    public void Select(string language)
    {
        _syncing = true;
        try { _picker.SelectedItem = _items[LanguagePreferences.Normalize(language)]; }
        finally { _syncing = false; }
    }
}
