using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal static class LocalizedUI
{
    private sealed class Subscription(Action refresh)
    {
        private readonly WeakReference<Action> _refresh = new(refresh);
        private bool _subscribed;
        public void Attach()
        {
            if (_subscribed) return;
            Localization.Changed += Refresh;
            _subscribed = true;
        }
        public void Detach()
        {
            if (!_subscribed) return;
            Localization.Changed -= Refresh;
            _subscribed = false;
        }
        private void Refresh()
        {
            if (_refresh.TryGetTarget(out var callback)) callback();
            else Detach();
        }
    }

    public static void Bind(FrameworkElement element, Action update)
    {
        void Refresh()
        {
            if (element.DispatcherQueue.HasThreadAccess) update();
            else element.DispatcherQueue.TryEnqueue(update.Invoke);
        }
        Action refresh = Refresh;
        var subscription = new Subscription(refresh);
        element.Loaded += (_, _) => { subscription.Attach(); refresh(); };
        element.Unloaded += (_, _) => subscription.Detach();
        refresh();
    }

    public static TextBlock Text(string key, double size = 14, bool secondary = false)
    {
        var text = IslandTheme.Text("", size, secondary);
        text.TextWrapping = TextWrapping.Wrap;
        Bind(text, () => text.Text = Localization.Get(key));
        return text;
    }

    public static void Label(FrameworkElement element, string key) => Bind(element, () =>
    {
        var label = Localization.Get(key);
        AutomationProperties.SetName(element, label);
        ToolTipService.SetToolTip(element, label);
    });
}
