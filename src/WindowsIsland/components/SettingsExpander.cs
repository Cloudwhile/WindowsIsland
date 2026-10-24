using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace WindowsIsland.Components;

// A list row with a trailing chevron. Child rows are indented in the same flat list
// instead of a native Expander card, so expandable rows look alike on every page.
internal sealed class SettingsExpander : StackPanel
{
    private const int ExpandGlyph = 0xE70D, CollapseGlyph = 0xE70E;
    private readonly IconActionButton _toggle;
    private readonly SettingsContent _content = new();
    private readonly Border _viewport = new() { Visibility = Visibility.Collapsed };
    private readonly RectangleGeometry _clip = new();
    private Storyboard? _animation;
    private bool _expanded;
    public SettingsRow Header { get; }
    public UIElementCollection Rows => _content.Children;
    public event Action<bool>? ExpandedChanged;
    /// <summary>是否允许点击标题区域展开详情，操作控件保持独立响应。</summary>
    public bool ExpandOnHeaderTap { get; set; }

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (IsExpanded == value) return;
            _expanded = value;
            Present();
            _toggle.SetAction(value ? CollapseGlyph : ExpandGlyph, value ? "ActionCollapse" : "ActionExpand");
            ExpandedChanged?.Invoke(value);
        }
    }

    public SettingsExpander(IconElement icon, string title, string automationId, string? statusId = null,
        bool localizeTitle = true, params FrameworkElement[] actions)
    {
        _toggle = new IconActionButton(ExpandGlyph, "ActionExpand", automationId + ".Toggle");
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in actions) toolbar.Children.Add(action);
        toolbar.Children.Add(_toggle);
        Header = new SettingsRow(icon, title, toolbar, statusId, localizeTitle);
        Header.Tapped += (_, args) =>
        {
            if (!ExpandOnHeaderTap) return;
            for (var element = args.OriginalSource as DependencyObject; element is not null && element != Header;
                element = VisualTreeHelper.GetParent(element))
                if (element is ButtonBase or ToggleSwitch) return;
            IsExpanded = !IsExpanded;
            args.Handled = true;
        };
        Children.Add(Header);
        _viewport.Child = _content;
        _viewport.Clip = _clip;
        _viewport.SizeChanged += (_, _) => UpdateClip();
        Children.Add(_viewport);
        _toggle.Click += (_, _) => IsExpanded = !IsExpanded;
        AutomationProperties.SetAutomationId(this, automationId);
        AutomationProperties.SetAutomationId(_viewport, automationId + ".Content");
        Loaded += (_, _) => { SettingsMotion.Changed += OnMotionChanged; Normalize(); };
        Unloaded += (_, _) => { SettingsMotion.Changed -= OnMotionChanged; Normalize(); };
        if (localizeTitle) LocalizedUI.Bind(this, () => AutomationProperties.SetName(this, Services.Localization.Get(title)));
    }

    private void Present()
    {
        var from = _viewport.Visibility == Visibility.Visible
            ? double.IsNaN(_viewport.Height) ? _viewport.ActualHeight : _viewport.Height : 0;
        Stop();
        if (!SettingsMotion.Allow(this) || !Timeline.AllowDependentAnimations) { Normalize(); return; }
        _viewport.Visibility = Visibility.Visible;
        _viewport.Height = double.NaN;
        _viewport.Measure(new Size(Math.Max(0, ActualWidth), double.PositiveInfinity));
        var to = _expanded ? _viewport.DesiredSize.Height : 0;
        if (Math.Abs(from - to) < 0.5) { Normalize(); return; }
        _viewport.Height = from;
        _viewport.IsHitTestVisible = _expanded;
        var animation = new Storyboard();
        animation.Children.Add(SettingsMotion.CreateAnimation(_viewport, "Height", from, to, dependent: true));
        animation.Completed += (_, _) => { if (ReferenceEquals(_animation, animation)) Normalize(); };
        _animation = animation;
        animation.Begin();
    }

    private void OnMotionChanged()
    {
        void Apply() { if (!SettingsMotion.Allow(this)) Normalize(); }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    private void Normalize()
    {
        Stop();
        _viewport.Height = double.NaN;
        _viewport.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _viewport.IsHitTestVisible = _expanded;
        UpdateClip();
    }

    private void Stop()
    {
        var animation = _animation;
        _animation = null;
        animation?.Stop();
    }

    private void UpdateClip() => _clip.Rect = new Rect(0, 0, Math.Max(0, _viewport.ActualWidth), Math.Max(0, _viewport.ActualHeight));
}
