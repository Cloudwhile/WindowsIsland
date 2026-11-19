using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

// 内置电源来源的电量额度提醒配置区：支持低电告警、紧急低电告警与充电上限提醒的即时持久化与多语言刷新。
internal sealed class PowerQuotaEditor : StackPanel
{
    private readonly IPluginSettings _settings;
    private readonly Slider _lowSlider, _criticalSlider, _chargeLimitSlider;
    private readonly ToggleSwitch _lowToggle, _criticalToggle, _chargeLimitToggle;
    private readonly SettingsRow _lowRow, _criticalRow, _chargeLimitRow;
    private readonly TextBlock _error;
    private bool _syncing;

    public PowerQuotaEditor(IPluginSettings settings)
    {
        _settings = settings;
        Spacing = 0;

        (_lowSlider, _lowToggle, _lowRow) = CreateQuotaRow(
            0xE852, "PowerLowQuota", "PowerQuotaStatus.Low",
            PowerQuotaSettings.LowPercentKey, PowerQuotaSettings.LowEnabledKey,
            PowerQuotaSettings.MinLowPercent, PowerQuotaSettings.MaxLowPercent);

        (_criticalSlider, _criticalToggle, _criticalRow) = CreateQuotaRow(
            0xE7BA, "PowerCriticalQuota", "PowerQuotaStatus.Critical",
            PowerQuotaSettings.CriticalPercentKey, PowerQuotaSettings.CriticalEnabledKey,
            PowerQuotaSettings.MinCriticalPercent, PowerQuotaSettings.MaxCriticalPercent);

        (_chargeLimitSlider, _chargeLimitToggle, _chargeLimitRow) = CreateQuotaRow(
            0xE85F, "PowerChargeLimitQuota", "PowerQuotaStatus.ChargeLimit",
            PowerQuotaSettings.ChargeLimitPercentKey, PowerQuotaSettings.ChargeLimitEnabledKey,
            PowerQuotaSettings.MinChargeLimitPercent, PowerQuotaSettings.MaxConfigurableChargeLimitPercent);

        Children.Add(_lowRow);
        Children.Add(_criticalRow);
        Children.Add(_chargeLimitRow);
        SettingsSection.UpdateSeparators(this);

        _error = LocalizedUI.Text("ErrorSettingsSave", 12, secondary: true);
        _error.TextWrapping = TextWrapping.Wrap;
        _error.Visibility = Visibility.Collapsed;
        Children.Add(SettingsContent.Wrap(_error));

        LoadFromSettings();
        LocalizedUI.Bind(this, RefreshText);

        _lowToggle.Toggled += (_, _) => OnChanged(EditedControl.LowToggle);
        _lowSlider.ValueChanged += OnLowValueChanged;
        _criticalToggle.Toggled += (_, _) => OnChanged(EditedControl.CriticalToggle);
        _criticalSlider.ValueChanged += OnCriticalValueChanged;
        _chargeLimitToggle.Toggled += (_, _) => OnChanged(EditedControl.ChargeLimit);
        _chargeLimitSlider.ValueChanged += OnChargeLimitValueChanged;
    }

    private enum EditedControl
    {
        LowToggle,
        LowSlider,
        CriticalToggle,
        CriticalSlider,
        ChargeLimit
    }

    private static (Slider Slider, ToggleSwitch Toggle, SettingsRow Row) CreateQuotaRow(
        int glyph, string titleKey, string statusId, string percentKey, string enabledKey, int min, int max)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = 1,
            SmallChange = 1,
            LargeChange = 5,
            Width = 136,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(slider, "PluginSetting." + BuiltInPluginIds.Power + "." + percentKey);

        var toggle = new ToggleSwitch
        {
            OnContent = null,
            OffContent = null,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(toggle, "PluginSetting." + BuiltInPluginIds.Power + "." + enabledKey);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = SettingsTokens.ControlSpacing,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(slider);
        actions.Children.Add(toggle);

        var icon = new FontIcon { Glyph = char.ConvertFromUtf32(glyph), FontSize = SettingsTokens.IconSize };
        var row = new SettingsRow(icon, titleKey, actions, statusId);
        return (slider, toggle, row);
    }

    private void OnLowValueChanged(object sender, RangeBaseValueChangedEventArgs e) => OnChanged(EditedControl.LowSlider);
    private void OnCriticalValueChanged(object sender, RangeBaseValueChangedEventArgs e) => OnChanged(EditedControl.CriticalSlider);
    private void OnChargeLimitValueChanged(object sender, RangeBaseValueChangedEventArgs e) => OnChanged(EditedControl.ChargeLimit);

    private void LoadFromSettings()
    {
        var quota = PowerQuotaSettings.From(_settings);
        _syncing = true;
        try
        {
            _lowToggle.IsOn = quota.LowEnabled;
            _lowSlider.Value = Math.Clamp(quota.LowPercent, PowerQuotaSettings.MinLowPercent, PowerQuotaSettings.MaxLowPercent);
            _criticalToggle.IsOn = quota.CriticalEnabled;
            _criticalSlider.Value = Math.Clamp(quota.CriticalPercent, PowerQuotaSettings.MinCriticalPercent, PowerQuotaSettings.MaxCriticalPercent);
            _chargeLimitToggle.IsOn = quota.ChargeLimitEnabled;
            _chargeLimitSlider.Value = Math.Clamp(quota.ChargeLimitPercent,
                PowerQuotaSettings.MinChargeLimitPercent, PowerQuotaSettings.MaxConfigurableChargeLimitPercent);
            SyncVisualOpacity();
        }
        finally { _syncing = false; }
    }

    private void OnChanged(EditedControl source)
    {
        if (_syncing) return;
        var lowEnabled = _lowToggle.IsOn;
        var criticalEnabled = _criticalToggle.IsOn;
        var chargeLimitEnabled = _chargeLimitToggle.IsOn;
        var low = (int)Math.Round(_lowSlider.Value);
        var critical = (int)Math.Round(_criticalSlider.Value);
        var limit = (int)Math.Round(_chargeLimitSlider.Value);

        if (lowEnabled && criticalEnabled && critical >= low)
        {
            if (source == EditedControl.CriticalSlider)
                low = Math.Min(PowerQuotaSettings.MaxLowPercent, critical + 5);
            else
                critical = Math.Max(PowerQuotaSettings.MinCriticalPercent, low - 5);
        }

        _syncing = true;
        try
        {
            if ((int)Math.Round(_lowSlider.Value) != low) _lowSlider.Value = low;
            if ((int)Math.Round(_criticalSlider.Value) != critical) _criticalSlider.Value = critical;
            SyncVisualOpacity();
        }
        finally { _syncing = false; }

        try
        {
            SetBoolIfChanged(PowerQuotaSettings.LowEnabledKey, lowEnabled, defaultValue: true);
            _settings.SetString(PowerQuotaSettings.LowPercentKey, low.ToString(CultureInfo.InvariantCulture));
            SetBoolIfChanged(PowerQuotaSettings.CriticalEnabledKey, criticalEnabled, defaultValue: true);
            _settings.SetString(PowerQuotaSettings.CriticalPercentKey, critical.ToString(CultureInfo.InvariantCulture));
            SetBoolIfChanged(PowerQuotaSettings.ChargeLimitEnabledKey, chargeLimitEnabled, defaultValue: false);
            _settings.SetString(PowerQuotaSettings.ChargeLimitPercentKey, limit.ToString(CultureInfo.InvariantCulture));
            _error.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            new PluginDiagnostics(BuiltInPluginIds.Power, DiagnosticArea.Application).Capture(error, "SettingsSave");
            LoadFromSettings();
            _error.Visibility = Visibility.Visible;
        }

        RefreshText();
    }

    private void SetBoolIfChanged(string key, bool value, bool defaultValue)
    {
        if (_settings.GetBoolean(key, defaultValue) != value)
            _settings.SetBoolean(key, value);
    }

    private void SyncVisualOpacity()
    {
        _lowSlider.Opacity = _lowToggle.IsOn ? 1.0 : 0.68;
        _criticalSlider.Opacity = _criticalToggle.IsOn ? 1.0 : 0.68;
        _chargeLimitSlider.Opacity = _chargeLimitToggle.IsOn ? 1.0 : 0.68;
    }

    private void RefreshText()
    {
        var low = (int)Math.Round(_lowSlider.Value);
        var critical = (int)Math.Round(_criticalSlider.Value);
        var limit = (int)Math.Round(_chargeLimitSlider.Value);

        ApplyRowText(_lowRow, _lowToggle, _lowSlider, "PowerLowQuota", "PowerLowQuotaDetail",
            _lowToggle.IsOn ? "PowerQuotaStatusBelowOn" : "PowerQuotaStatusBelowOff", low);
        ApplyRowText(_criticalRow, _criticalToggle, _criticalSlider, "PowerCriticalQuota", "PowerCriticalQuotaDetail",
            _criticalToggle.IsOn ? "PowerQuotaStatusBelowOn" : "PowerQuotaStatusBelowOff", critical);
        ApplyRowText(_chargeLimitRow, _chargeLimitToggle, _chargeLimitSlider, "PowerChargeLimitQuota", "PowerChargeLimitQuotaDetail",
            _chargeLimitToggle.IsOn ? "PowerQuotaStatusAboveOn" : "PowerQuotaStatusAboveOff", limit);
    }

    private static void ApplyRowText(SettingsRow row, ToggleSwitch toggle, Slider slider,
        string titleKey, string detailKey, string statusFormatKey, int percent)
    {
        var title = Localization.Get(titleKey);
        row.Status = Localization.Format(statusFormatKey, percent);
        row.Detail = Localization.Get(detailKey);
        AutomationProperties.SetName(toggle, Localization.Format(toggle.IsOn ? "SourceEnabledLabel" : "SourceDisabledLabel", title));
        AutomationProperties.SetName(slider, Localization.Format("PowerQuotaThresholdLabel", title));
    }
}
