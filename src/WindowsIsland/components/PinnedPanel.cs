using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class PinnedPanel : Grid
{
    private readonly Grid _header = new() { ColumnSpacing = 12 };
    private readonly TextBlock _value = IslandTheme.Text("", 18);
    private readonly TextBlock _title = IslandTheme.Text("", 14);
    private readonly TextBlock _body = IslandTheme.Text("", 13, secondary: true);
    private readonly StackPanel _details = new() { Spacing = 12, Padding = new Thickness(0, 0, 4, 2) };
    private readonly StackPanel _controls = new() { Spacing = 12 };
    private readonly ContentControl _custom = new() { IsTabStop = false };
    private readonly ScrollViewer _scroll;
    private PinnedContent? _content;
    private PinnedControl[] _models = [];
    private string? _xaml;
    private bool _refreshing;
    private FrameworkElement? _pressed;
    private bool _scrolling;
    public bool LayoutChanged { get; private set; }
    public event Action<PinnedControlInteraction>? Interacted;
    public event Action? Dismissed;

    public PinnedPanel()
    {
        RowSpacing = 12;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new ColumnDefinition());
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _value.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _value.FontWeight = FontWeights.SemiBold;
        var close = new IconActionButton(Symbol.Cancel, "ActionClose", "PinnedPanelClose") { AllowFocusOnInteraction = false };
        close.Click += (_, _) => Dismissed?.Invoke();
        Grid.SetColumn(close, 1);
        _header.Children.Add(_value); _header.Children.Add(close);
        _title.FontWeight = FontWeights.SemiBold;
        _title.TextWrapping = TextWrapping.Wrap;
        _body.TextWrapping = TextWrapping.Wrap;
        _body.LineHeight = 19;
        _body.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _body.IsTextSelectionEnabled = true;
        _details.Children.Add(_title); _details.Children.Add(_body); _details.Children.Add(_controls); _details.Children.Add(_custom);
        _scroll = new ScrollViewer
        {
            Content = _details, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsTabStop = false
        };
        Children.Add(_header); Children.Add(_scroll);
        AutomationProperties.SetAutomationId(this, "PinnedPanel");
    }

    public void Present(PinnedContent content, NotificationPosition position)
    {
        _refreshing = true;
        try
        {
            LayoutChanged = _content?.Body != content.Body || _content?.Title != content.Title
                || !_models.Select(Shape).SequenceEqual((content.Controls ?? []).Select(Shape)) || _xaml != content.CustomPanelXaml;
            _content = content;
            _value.Text = content.CompactText.ReplaceLineEndings(" ");
            _title.Text = content.Title;
            _body.Text = content.Body ?? "";
            _body.Visibility = string.IsNullOrWhiteSpace(content.Body) ? Visibility.Collapsed : Visibility.Visible;
            var bottom = NotificationPlacement.Anchor(position).Y == 1;
            RowDefinitions[0].Height = bottom ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            RowDefinitions[1].Height = bottom ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            Grid.SetRow(_header, bottom ? 1 : 0);
            Grid.SetRow(_scroll, bottom ? 0 : 1);
            var models = content.Controls?.ToArray() ?? [];
            var rebuild = !_models.Select(Shape).SequenceEqual(models.Select(Shape)) || _xaml != content.CustomPanelXaml;
            _models = models;
            if (rebuild)
            {
                _pressed = null;
                _scrolling = false;
                _controls.Children.Clear();
                foreach (var model in models) _controls.Children.Add(CreateControl(model));
                _xaml = content.CustomPanelXaml;
                _custom.Content = null;
                if (!string.IsNullOrWhiteSpace(_xaml))
                {
                    try
                    {
                        // 来源策略由 PluginManager 检查，数据控件事件仍经宿主绑定。
                        var custom = XamlReader.Load(_xaml) as FrameworkElement ?? throw new InvalidDataException();
                        _custom.Content = custom;
                        foreach (var model in models)
                            if (custom.FindName(model.Id) is FrameworkElement element) BindControl(element, model);
                    }
                    catch (Exception error)
                    {
                        _custom.Content = null;
                        Diagnostics.Capture(DiagnosticArea.Application, error, "PinnedPanelXaml");
                    }
                }
            }
            foreach (var element in _controls.Children.OfType<FrameworkElement>()) RefreshControl(element);
            _controls.Visibility = _custom.Content is null ? Visibility.Visible : Visibility.Collapsed;
            if (_custom.Content is FrameworkElement customRoot)
                foreach (var model in models)
                    if (customRoot.FindName(model.Id) is FrameworkElement element) RefreshControl(element, model);
        }
        finally { _refreshing = false; }
    }
    private static object Shape(PinnedControl control) => (control.Id, control.Kind, control.Label, control.Symbol);
    private FrameworkElement CreateControl(PinnedControl model)
    {
        FrameworkElement element = model.Kind switch
        {
            PinnedControlKind.Button => new Button { Content = model.Symbol == "refresh"
                ? new FontIcon { Glyph = "\uE72C", FontSize = 16 } : model.Label, HorizontalAlignment = HorizontalAlignment.Left },
            PinnedControlKind.Toggle => new ToggleSwitch { Header = model.Label, HorizontalAlignment = HorizontalAlignment.Stretch },
            _ => new Slider { Header = model.Label, HorizontalAlignment = HorizontalAlignment.Stretch }
        };
        BindControl(element, model);
        return element;
    }
    private void BindControl(FrameworkElement element, PinnedControl model)
    {
        element.Tag = model.Id;
        AutomationProperties.SetAutomationId(element, "PinnedControl." + model.Id);
        AutomationProperties.SetName(element, model.Label);
        ToolTipService.SetToolTip(element, model.Label);
        if (element is Control control) control.AllowFocusOnInteraction = false;
        switch (element)
        {
            case Button button when model.Kind == PinnedControlKind.Button:
                button.Click += (_, _) => Send(model.Id); break;
            case ToggleSwitch toggle when model.Kind == PinnedControlKind.Toggle:
                toggle.Toggled += (_, _) => Send(model.Id, toggle.IsOn); break;
            case Slider slider when model.Kind == PinnedControlKind.Slider:
                slider.ValueChanged += (_, args) => Send(model.Id, value: args.NewValue); break;
        }
        RefreshControl(element, model);
    }
    private void RefreshControl(FrameworkElement element, PinnedControl? model = null)
    {
        model ??= _models.FirstOrDefault(item => item.Id == element.Tag as string);
        if (model is null) return;
        if (element is Control control) control.IsEnabled = model.IsEnabled;
        if (element is ToggleSwitch toggle) toggle.IsOn = model.IsOn;
        if (element is Slider slider)
        {
            slider.Minimum = model.Minimum;
            slider.Maximum = model.Maximum;
            slider.StepFrequency = model.Step;
            slider.Value = model.Value;
        }
    }
    private void Send(string id, bool? on = null, double? value = null)
    {
        if (_refreshing || _content is null) return;
        Interacted?.Invoke(new(_content.Id, id, on, value));
    }
    public Size MeasureWindow(double availableWidth, double availableHeight)
    {
        var width = Math.Max(1, Math.Min(420, availableWidth));
        var height = Math.Max(1, Math.Min(620, availableHeight));
        var innerWidth = Math.Max(1, width - NotificationLayout.HorizontalPadding * 2);
        Width = innerWidth;
        EnsureTemplates(this);
        _header.Measure(new Size(innerWidth, double.PositiveInfinity));
        var headerHeight = Math.Max(36, Math.Ceiling(_header.DesiredSize.Height));
        var maxScroll = Math.Max(48, height - NotificationLayout.VerticalPadding * 2 - headerHeight - RowSpacing);
        _scroll.MaxHeight = maxScroll;
        _details.Measure(new Size(innerWidth, double.PositiveInfinity));
        var detailsHeight = Math.Ceiling(_details.DesiredSize.Height);
        if (_controls.Visibility == Visibility.Visible && _controls.Children.Count > 0)
        {
            var measuredControls = 0d;
            var minimumControls = 0d;
            foreach (var child in _controls.Children.OfType<FrameworkElement>())
            {
                child.Measure(new Size(innerWidth, double.PositiveInfinity));
                measuredControls += child.DesiredSize.Height;
                minimumControls += Math.Max(child.DesiredSize.Height, MinimumControlHeight(child));
            }
            detailsHeight += Math.Ceiling(Math.Max(0, minimumControls - measuredControls));
        }
        var scrollHeight = Math.Min(maxScroll, detailsHeight);
        Measure(new Size(innerWidth, headerHeight + RowSpacing + scrollHeight));
        return new Size(width, Math.Min(height, Math.Ceiling(headerHeight + RowSpacing + scrollHeight + NotificationLayout.VerticalPadding * 2)));
    }

    private static double MinimumControlHeight(FrameworkElement element) => element switch
    {
        ToggleSwitch { Header: not null } => 62,
        ToggleSwitch => 36,
        Slider { Header: not null } => 62,
        Slider => 36,
        Button => 32,
        _ => 0
    };

    private static void EnsureTemplates(DependencyObject root)
    {
        if (root is Control control) control.ApplyTemplate();
        switch (root)
        {
            case Panel panel:
                foreach (var child in panel.Children) EnsureTemplates(child);
                break;
            case ScrollViewer { Content: DependencyObject content }:
                EnsureTemplates(content);
                break;
            case ContentControl { Content: DependencyObject content }:
                EnsureTemplates(content);
                break;
            case Border { Child: DependencyObject child }:
                EnsureTemplates(child);
                break;
        }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++) EnsureTemplates(VisualTreeHelper.GetChild(root, index));
    }

    // 复用既有不激活输入层。原生控件继续负责呈现、自动化与值事件，鼠标不激活 WinUI 窗口。
    public void HandlePointer(FrameworkElement host, Point point, NotificationPointerKind kind, int delta)
    {
        if (_pressed is Control { IsEnabled: false }) _pressed = null;
        Rect Bounds(FrameworkElement element)
        {
            var origin = element.TransformToVisual(host).TransformPoint(new Point());
            return new Rect(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
        }
        if (kind == NotificationPointerKind.Wheel)
        {
            Scroll(delta);
            return;
        }
        if (kind == NotificationPointerKind.Pressed)
        {
            _pressed = null;
            var elements = _custom.Content is FrameworkElement custom
                ? _models.Select(model => custom.FindName(model.Id)).OfType<FrameworkElement>()
                : _controls.Children.OfType<FrameworkElement>();
            // 关闭按钮仍使用原生按钮，同样通过已有输入层转交操作。
            var scrollBounds = Bounds(_scroll);
            var close = _header.Children.OfType<Button>().Single();
            _pressed = elements.Where(_ => scrollBounds.Contains(point)).Append(close)
                .FirstOrDefault(element => element is Control { IsEnabled: true } && Bounds(element).Contains(point));
            _scrolling = _pressed is null && _scroll.ScrollableHeight > 0 && scrollBounds.Contains(point) && point.X > scrollBounds.Right - 18;
            if (_pressed is Control control) VisualStateManager.GoToState(control, "Pressed", false);
        }
        if (_pressed is Slider slider && kind is NotificationPointerKind.Pressed or NotificationPointerKind.Moved or NotificationPointerKind.Released)
        {
            var bounds = Bounds(slider);
            var ratio = Math.Clamp((point.X - bounds.X - 12) / Math.Max(1, bounds.Width - 24), 0, 1);
            var value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
            slider.Value = ratio == 1 ? slider.Maximum : Math.Clamp(slider.Minimum + Math.Round((value - slider.Minimum) / slider.StepFrequency) * slider.StepFrequency, slider.Minimum, slider.Maximum);
        }
        else if (_scrolling)
        {
            var bounds = Bounds(_scroll);
            _scroll.ChangeView(null, Math.Clamp((point.Y - bounds.Y) / Math.Max(1, bounds.Height), 0, 1) * _scroll.ScrollableHeight, null, true);
        }
        if (kind != NotificationPointerKind.Released) return;
        if (_pressed is { } pressed && Bounds(pressed).Contains(point))
        {
            if (pressed is ToggleSwitch toggle) toggle.IsOn = !toggle.IsOn;
            else if (pressed is Button)
            {
                if (pressed.Tag is string id) Send(id);
                else Dismissed?.Invoke();
            }
        }
        if (_pressed is Control released) VisualStateManager.GoToState(released, "Normal", false);
        _pressed = null;
        _scrolling = false;
    }
    public void Scroll(int delta) => _scroll.ChangeView(null,
        Math.Clamp(_scroll.VerticalOffset - delta / 120d * 60, 0, _scroll.ScrollableHeight), null, true);
}
