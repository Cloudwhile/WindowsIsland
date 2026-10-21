using System.Numerics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using WindowsIsland.Components;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland;

public sealed partial class MainWindow
{
    private IslandState _presentedState;
    private IslandNotification? _presentedMessage;
    private bool _presentedPinned;
    private IslandExpandedKind _presentedExpanded;

    private void ScrollPinnedPanel(int delta)
    {
        if (!_closed && _scheduler.ExpandedKind == IslandExpandedKind.Pinned) _islandContent.Panel.Scroll(delta);
    }

    private void HandlePanelPointer(NotificationPointer pointer)
    {
        if (_closed || _scheduler.ExpandedKind != IslandExpandedKind.Pinned || _lastBounds is not { } bounds) return;
        if (pointer.Kind == NotificationPointerKind.RightClick) { DismissPinnedPanel(); return; }
        _islandContent.Panel.HandlePointer(_host, new Point((pointer.X - bounds.X) / _scale, (pointer.Y - bounds.Y) / _scale), pointer.Kind, pointer.Delta);
    }

    private void HandlePinnedInteraction(PinnedControlInteraction interaction)
    {
        if (_scheduler.ExpandedKind == IslandExpandedKind.Pinned && _scheduler.Pinned is { } pinned)
            _ = _plugins.InteractPinnedAsync(pinned, interaction);
    }

    private void HandleIslandInput(bool right)
    {
        if (_closed) return;
        if (_scheduler.ExpandedKind == IslandExpandedKind.Message)
        {
            if (right) DismissNotification();
            else _ = OpenNotificationSourceAsync();
        }
        else if (_scheduler.State == IslandState.Compact && _scheduler.Pinned is { } pinned)
        {
            if (right) { _scheduler.PinnedRemoved(pinned.PluginId, pinned.Id); PresentSchedule(); }
            else { _scheduler.PinnedClicked(); PresentSchedule(); }
        }
    }

    private void DismissPinnedPanel()
    {
        if (_closed || _scheduler.ExpandedKind != IslandExpandedKind.Pinned) return;
        _scheduler.PanelDismissed();
        PresentSchedule();
    }

    private void ReceivePinned(PinnedContentChange change)
    {
        if (_closed) return;
        switch (change.Kind)
        {
            case PinnedChangeKind.Added: _scheduler.PinnedAdded(change.Content!); break;
            case PinnedChangeKind.Updated: _scheduler.PinnedUpdated(change.Content!); break;
            case PinnedChangeKind.Removed: _scheduler.PinnedRemoved(change.PluginId, change.Id); break;
        }
        PresentSchedule();
    }

    // 业务状态仅来自调度器；这里的缓存只用于避免每秒文本更新重启动画。
    private void PresentSchedule(bool newMessage = false, bool animate = true, bool force = false)
    {
        if (_closed) return;
        if (_scheduler.HasContent) _expiry.Start();
        else _expiry.Stop();
        var state = _scheduler.State;
        var pinned = _scheduler.Pinned is not null;
        var expanded = _scheduler.ExpandedKind;
        var changed = state != _presentedState || expanded != _presentedExpanded || pinned != _presentedPinned || _current != _presentedMessage;
        _presentedExpanded = expanded;
        _presentedState = state;
        _presentedPinned = pinned;
        _presentedMessage = _current;
        if (expanded != IslandExpandedKind.Pinned)
        {
            _panelDismiss.Hide();
            _input.PointerInput = null;
            _islandContent.ClearPanel();
            NativeWindow.ConfigureOverlay(_hwnd);
        }
        if (state == IslandState.Minimal)
        {
            _surfaceVisible = false;
            _input.Hide();
            if (AppWindow.IsVisible && (changed || force)) _motion.Hide(HideIsland, animate);
            else if (!AppWindow.IsVisible || !animate || !_settings.Current.Animations) HideIsland();
            return;
        }
        _islandContent.Present(state, _scheduler.Pinned, expanded, _position);
        if (expanded == IslandExpandedKind.Pinned && _islandContent.Panel.LayoutChanged) changed = true;
        if (!changed && !force) return;
        RefreshDisplayMetrics();
        Size target;
        if (expanded == IslandExpandedKind.Pinned)
        {
            target = _islandContent.Panel.MeasureWindow(_workArea.Width / _scale - 24, _workArea.Height / _scale - 24);
            _root.Padding = new Thickness(16);
        }
        else if (expanded == IslandExpandedKind.Message)
        {
            _notificationPanel.Show(_current!);
            var inset = pinned ? IslandContent.PinnedInset * 2 : 0;
            var measured = _notificationPanel.MeasureWindow(Math.Min(NotificationLayout.MaxWidth, _workArea.Width / _scale - 24) - inset,
                _workArea.Height / _scale - 24);
            target = new Size(measured.Width + inset, measured.Height);
            _root.Padding = new Thickness(16);
        }
        else
        {
            _notificationPanel.Clear();
            target = new Size(Math.Min(IslandContent.CompactWidth, _workArea.Width / _scale - 24), IslandContent.CompactHeight);
            _root.Padding = new Thickness(12, 6, 12, 6);
        }
        _input.Hide();
        var wasVisible = AppWindow.IsVisible;
        var previous = new Size(_width, _height);
        _width = target.Width;
        _height = target.Height;
        _root.MaxHeight = Math.Max(NotificationLayout.MaxHeight, Math.Max(_surfaceLayer.Height, target.Height));
        _islandContent.SetContentWidth(target.Width - (state == IslandState.Compact ? 26 : NotificationLayout.HorizontalPadding * 2), state, pinned);
        _root.Width = _surfaceLayer.Width = Math.Max(_surfaceLayer.Width, target.Width);
        _root.Height = _surfaceLayer.Height = Math.Max(_surfaceLayer.Height, target.Height);
        _surfaceVisible = true;
        ApplyBounds();
        if (!wasVisible)
        {
            AppWindow.Show(false);
            NativeWindow.RemoveSystemBorder(_hwnd);
        }
        NativeWindow.ConfigureOverlay(_hwnd);
        _host.UpdateLayout();
        _motion.Show(previous, target, wasVisible, newMessage, animate);
        if (expanded == IslandExpandedKind.Pinned) UpdateInputRegion(_width, _height);
    }

    private void DismissNotification(bool animate = true)
    {
        if (_closed) return;
        _scheduler.MessageDismissed();
        PresentSchedule(animate: animate);
    }

    private void HideIsland()
    {
        if (_closed || _scheduler.State != IslandState.Minimal) return;
        _motion.Reset();
        _surfaceVisible = false;
        _width = MinimalWidth;
        _height = MinimalHeight;
        _notificationPanel.Clear();
        _islandContent.Present(IslandState.Minimal, null);
        _input.Hide();
        AppWindow.Hide();
    }

    private void FinishPresentation()
    {
        if (_closed || !_surfaceVisible) return;
        _root.Width = _surfaceLayer.Width = _width;
        _root.Height = _surfaceLayer.Height = _height;
        _root.MaxHeight = Math.Max(NotificationLayout.MaxHeight, _height);
        ApplyBounds();
        _host.UpdateLayout();
        UpdateInputRegion(_width, _height);
    }

    private void UpdateInputRegion(double width, double height)
    {
        if (_closed || !AppWindow.IsVisible || _lastBounds is not { } bounds) return;
        var contentWidth = (int)Math.Ceiling(width * _scale);
        var contentHeight = (int)Math.Ceiling(height * _scale);
        var offset = ContentOffset(bounds.Width, bounds.Height, width, height);
        var region = new RectInt32(bounds.X + (int)Math.Round(offset.X),
            bounds.Y + (int)Math.Round(offset.Y), contentWidth, contentHeight);
        if (_scheduler.ExpandedKind == IslandExpandedKind.Pinned)
        {
            _input.PointerInput = pointer => DispatcherQueue.TryEnqueue(() => HandlePanelPointer(pointer));
            try { _panelDismiss.Show(region); }
            catch (Exception error)
            {
                Diagnostics.Capture(DiagnosticArea.Application, error, "PanelDismissInput");
                DispatcherQueue.TryEnqueue(DismissPinnedPanel);
            }
        }
        else _input.PointerInput = null;
        _input.Show(region, (int)Math.Ceiling(NotificationLayout.CornerRadius * _scale));
    }

    private void ApplyBounds()
    {
        var anchor = NotificationPlacement.Anchor(_position);
        _surfaceLayer.HorizontalAlignment = _islandContent.HorizontalAlignment = anchor.X switch
        {
            0 => HorizontalAlignment.Left, 1 => HorizontalAlignment.Right, _ => HorizontalAlignment.Center
        };
        _surfaceLayer.VerticalAlignment = _islandContent.VerticalAlignment = anchor.Y switch
        {
            0 => VerticalAlignment.Top, 1 => VerticalAlignment.Bottom, _ => VerticalAlignment.Center
        };
        var width = Math.Max(1, (int)Math.Round(Math.Min(NotificationLayout.MaxWidth + SurfaceMargin * 2, _workArea.Width / _scale) * _scale));
        var height = Math.Max(1, (int)Math.Round(Math.Min(Math.Max(NotificationLayout.MaxHeight, _surfaceLayer.Height) + SurfaceMargin * 2, _workArea.Height / _scale) * _scale));
        var origin = NotificationPlacement.Offset(_position, new(_workArea.Width, _workArea.Height), new(width, height));
        var x = _workArea.X + (int)Math.Round(origin.X);
        var y = _workArea.Y + (int)Math.Round(origin.Y);
        var bounds = new RectInt32(x, y, width, height);
        if (_lastBounds != bounds)
        {
            AppWindow.MoveAndResize(bounds);
            _lastBounds = bounds;
        }
        var contentOffset = ContentOffset(width, height, _width, _height);
        NativeWindow.SetContentBounds(_hwnd, (int)Math.Round(contentOffset.X), (int)Math.Round(contentOffset.Y),
            (int)Math.Ceiling(_width * _scale), (int)Math.Ceiling(_height * _scale), (int)(NotificationLayout.CornerRadius * _scale));
        if (AppWindow.IsVisible) NativeWindow.EnsureTopmost(_hwnd);
    }

    private Vector2 ContentOffset(int hostWidth, int hostHeight, double width, double height) =>
        NotificationPlacement.Offset(_position, new(hostWidth, hostHeight),
            new((float)(width * _scale), (float)(height * _scale)), (float)(SurfaceMargin * _scale));

    private void RefreshDisplayMetrics()
    {
        _scale = Math.Max(1, NativeWindow.GetDpiForWindow(_hwnd) / 96d);
        _workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
    }

}
