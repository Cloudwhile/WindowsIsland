using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsAppearance : StackPanel, IDisposable
{
    private readonly SettingsStore _store;
    private readonly Action<Func<AppSettings, AppSettings>> _save;
    private readonly ToggleSwitch _acrylic = Toggle("Acrylic", "AcrylicToggle");
    private readonly ToggleSwitch _transitions = Toggle("OpacityTransitions", "OpacityTransitionsToggle");
    private readonly Slider _transparency = new()
    {
        Minimum = 0, Maximum = 80, StepFrequency = 1, SmallChange = 5, LargeChange = 10,
        Width = 180, MaxWidth = 240, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly SettingsRow _acrylicRow, _transitionsRow, _transparencyRow;
    private bool _syncing, _disposed;

    public SettingsAppearance(SettingsStore store, Action<Func<AppSettings, AppSettings>> save,
        SettingsRow? animationsRow = null)
    {
        _store = store;
        _save = save;
        AutomationProperties.SetAutomationId(_transparency, "BackgroundTransparencySlider");
        LocalizedUI.Label(_transparency, "BackgroundTransparency");
        _acrylicRow = new SettingsRow(Symbol.View, "Acrylic", _acrylic);
        _transitionsRow = new SettingsRow(Symbol.SlideShow, "OpacityTransitions", _transitions);
        _transparencyRow = new SettingsRow(Symbol.Edit, "BackgroundTransparency", _transparency, "BackgroundTransparencyValue");
        Children.Add(animationsRow is null
            ? new SettingsSection("AppearanceMotion", _transitionsRow)
            : new SettingsSection("AppearanceMotion", animationsRow, _transitionsRow));
        Children.Add(new SettingsSection("AppearanceSurface", _acrylicRow, _transparencyRow));
        _acrylic.Toggled += OnAcrylicChanged;
        _transitions.Toggled += OnTransitionsChanged;
        _transparency.ValueChanged += OnTransparencyChanged;
    }

    private static ToggleSwitch Toggle(string label, string id)
    {
        var toggle = new ToggleSwitch
        {
            OnContent = null, OffContent = null, MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right
        };
        AutomationProperties.SetAutomationId(toggle, id);
        LocalizedUI.Label(toggle, label);
        return toggle;
    }

    public void Refresh(bool enabled)
    {
        if (_disposed) return;
        var settings = _store.Current;
        _syncing = true;
        try
        {
            _acrylic.IsOn = settings.Acrylic;
            _transitions.IsOn = settings.OpacityTransitions;
            _transparency.Value = Math.Round((1 - settings.BackgroundOpacity) * 100);
            _acrylicRow.Status = Localization.Get(settings.Acrylic ? "On" : "Off");
            _transitionsRow.Status = Localization.Get(settings.OpacityTransitions ? "On" : "Off");
            _transparencyRow.Status = Localization.Format("TransparencyPercent", _transparency.Value);
            _acrylic.IsEnabled = _transitions.IsEnabled = _transparency.IsEnabled = enabled;
        }
        finally { _syncing = false; }
    }

    private void OnAcrylicChanged(object sender, RoutedEventArgs args)
    {
        if (!_syncing && !_disposed) _save(settings => settings with { Acrylic = _acrylic.IsOn });
    }

    private void OnTransitionsChanged(object sender, RoutedEventArgs args)
    {
        if (!_syncing && !_disposed) _save(settings => settings with { OpacityTransitions = _transitions.IsOn });
    }

    private void OnTransparencyChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_syncing && !_disposed)
            _save(settings => settings with { BackgroundOpacity = Math.Round(1 - args.NewValue / 100, 2) });
    }

    public void Dispose()
    {
        _disposed = true;
        _acrylic.Toggled -= OnAcrylicChanged;
        _transitions.Toggled -= OnTransitionsChanged;
        _transparency.ValueChanged -= OnTransparencyChanged;
    }
}
