using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace WindowsIsland.Components;

internal sealed class AppIcon : Grid
{
    public const double IconSize = 40;
    private readonly Border _image = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _fallback = IslandTheme.Text("", 24);
    private readonly Border _placeholder = (Border)XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource ControlFillColorSecondaryBrush}' />");
    private bool _circular;
    private int _version;
    private byte[]? _bytes;
    private int _decodeWidth;
    private string _appName = "";
    private string? _symbol;
    private XamlRoot? _observedRoot;

    public AppIcon(double size = IconSize, string automationId = "NotificationAppIcon")
    {
        Width = Height = size;
        VerticalAlignment = VerticalAlignment.Center;
        _fallback.FontSize = size * 0.52;
        _fallback.HorizontalAlignment = HorizontalAlignment.Center;
        _fallback.TextAlignment = TextAlignment.Center;
        _fallback.MaxLines = 1;
        _placeholder.Child = _fallback;
        Children.Add(_placeholder);
        Children.Add(_image);
        AutomationProperties.SetAccessibilityView(_fallback, AccessibilityView.Raw);
        SetAutomationId(automationId);
        Loaded += OnLoaded;
        Unloaded += (_, _) => ObserveRoot(null);
        SizeChanged += (_, _) => RefreshDecode();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        ObserveRoot(XamlRoot);
        RefreshDecode();
    }

    private void ObserveRoot(XamlRoot? root)
    {
        if (ReferenceEquals(root, _observedRoot)) return;
        if (_observedRoot is not null) _observedRoot.Changed -= RootChanged;
        _observedRoot = root;
        if (_observedRoot is not null) _observedRoot.Changed += RootChanged;
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshDecode();
    private int PhysicalWidth => Math.Max(1, (int)Math.Ceiling(Width * (XamlRoot?.RasterizationScale ?? 1)));
    private void RefreshDecode()
    {
        if (_bytes is { Length: > 0 } && _decodeWidth != PhysicalWidth)
            _ = ShowAsync(_appName, _bytes, _symbol);
    }

    public void SetAutomationId(string value) => AutomationProperties.SetAutomationId(this, value);

    public void SetCircular(bool circular)
    {
        _circular = circular;
        CornerRadius = _image.CornerRadius = new CornerRadius(circular ? Width / 2 : 0);
        _placeholder.CornerRadius = new CornerRadius(circular ? Width / 2 : 4);
        if (_image.Background is ImageBrush brush) brush.Stretch = circular ? Stretch.UniformToFill : Stretch.Uniform;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new IconAutomationPeer(this);

    public async Task ShowAsync(string appName, byte[]? bytes, string? symbol = null)
    {
        AutomationProperties.SetName(this, appName);
        _appName = appName;
        _symbol = symbol;
        var decodeWidth = PhysicalWidth;
        if (bytes is { Length: > 0 } && ReferenceEquals(bytes, _bytes) && _decodeWidth == decodeWidth
            && _image.Visibility == Visibility.Visible) return;
        _decodeWidth = decodeWidth;
        _bytes = bytes;
        var version = ++_version;
        _image.Background = null;
        _image.Visibility = Visibility.Collapsed;
        _placeholder.Visibility = Visibility.Visible;
        _fallback.Visibility = Visibility.Visible;
        _fallback.Text = symbol ?? (string.IsNullOrWhiteSpace(appName) ? "?" : appName.Trim().EnumerateRunes().First().ToString().ToUpperInvariant());
        if (bytes is null || bytes.Length == 0) return;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Physical, DecodePixelWidth = decodeWidth };
            await bitmap.SetSourceAsync(stream);
            if (version != _version) return;
            _image.Background = new ImageBrush
            {
                ImageSource = bitmap,
                Stretch = _circular ? Stretch.UniformToFill : Stretch.Uniform
            };
            _image.Visibility = Visibility.Visible;
            _fallback.Visibility = Visibility.Collapsed;
            _placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            // Keep the app initial if Windows cannot supply a usable logo.
        }
    }

    public void Clear()
    {
        _version++;
        _bytes = null;
        _image.Background = null;
        _image.Visibility = Visibility.Collapsed;
        _fallback.Text = "";
        _fallback.Visibility = Visibility.Collapsed;
        _placeholder.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(this, "");
    }

    private sealed class IconAutomationPeer(AppIcon owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(AppIcon);
    }
}
