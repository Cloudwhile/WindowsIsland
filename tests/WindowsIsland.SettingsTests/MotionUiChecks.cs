using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;
using WindowsIsland.Components;
using WindowsIsland.Services;

namespace WindowsIsland;

// Compiled only into the isolated native settings verification application.
internal static class MotionUiChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        SettingsMotion.SetEnabled(root, true);
        var expander = new SettingsExpander(new SymbolIcon(Symbol.Document), "DiagnosticsEvents", "MotionFixture");
        var text = new TextBlock { Text = "First fixed verification line.\nSecond fixed verification line.", TextWrapping = TextWrapping.Wrap };
        expander.Rows.Add(text);
        root.Children.Add(expander);
        var viewport = expander.Children.OfType<Border>().Single(border => AutomationProperties.GetAutomationId(border) == "MotionFixture.Content");
        var settings = new SettingsStore(Path.Combine(AppContext.BaseDirectory, "verification-motion-settings.json"));
        settings.Save(new(Animations: false, SystemNotifications: false, Position: NotificationPosition.BottomRight));
        var actualPreviews = 0;
        using var preview = new NotificationAppearancePreview(settings, () => actualPreviews++);
        root.Children.Add(preview);
        var window = new Window { Content = new ScrollViewer { Content = root } };
        window.AppWindow.Resize(new(600, 760));
        try
        {
            check(!SettingsMotion.Allow(expander) && !SettingsMotion.Allows(true, true, false)
                && !SettingsMotion.Allows(true, false, true) && !SettingsMotion.Allows(false, true, true),
                "Settings motion rejects unloaded controls, app-disabled motion and the system-disabled policy branch without changing system settings");
            var animation = SettingsMotion.CreateAnimation(viewport, "Height", 0, 100, dependent: true);
            check(animation.Duration.TimeSpan.TotalMilliseconds == 160 && animation.EnableDependentAnimation
                && animation.EasingFunction is CubicEase { EasingMode: EasingMode.EaseOut },
                "Shared settings motion uses 160 milliseconds and cubic ease-out, explicitly enabling only dependent height animations");
            window.Activate();
            await Until(() => expander.IsLoaded && preview.IsLoaded && expander.ActualWidth > 0);
            SettingsMotion.SetEnabled(root, false);
            expander.IsExpanded = true;
            check(double.IsNaN(viewport.Height) && viewport.Visibility == Visibility.Visible && Active(expander) is null,
                "App-disabled expansion completes immediately with automatic height");
            root.UpdateLayout();
            var originalHeight = viewport.ActualHeight;
            text.Text += "\nThird fixed verification line.\nFourth fixed verification line.";
            root.UpdateLayout();
            check(viewport.ActualHeight > originalHeight && double.IsNaN(viewport.Height),
                "Expanded automatic height accommodates later content and language-sized text");

            SettingsMotion.SetEnabled(root, true);
            var systemAllows = new UISettings().AnimationsEnabled;
            check(SettingsMotion.Allow(expander) == systemAllows, "Loaded motion follows the actual system animation preference");
            expander.IsExpanded = false;
            if (systemAllows && Timeline.AllowDependentAnimations)
                check(Active(expander) is not null && !double.IsNaN(viewport.Height),
                    "Enabled collapse animates the actual layout height instead of only switching visibility");
            for (var index = 0; index < 7; index++) expander.IsExpanded = !expander.IsExpanded;
            expander.IsExpanded = true;
            await Until(() => Active(expander) is null && double.IsNaN(viewport.Height));
            root.UpdateLayout();
            check(expander.IsExpanded && viewport.Visibility == Visibility.Visible && viewport.ActualHeight > 0
                && viewport.Clip is RectangleGeometry clip && Math.Abs(clip.Rect.Height - viewport.ActualHeight) < 0.5,
                "Rapid expand/collapse keeps the final requested state and clips content to the current layout bounds");
            expander.IsExpanded = false;
            SettingsMotion.SetEnabled(root, false);
            check(!expander.IsExpanded && viewport.Visibility == Visibility.Collapsed && double.IsNaN(viewport.Height)
                && Active(expander) is null, "Turning off app motion immediately finishes an in-progress collapse");

            SettingsMotion.SetEnabled(root, true);
            expander.IsExpanded = true;
            var unloaded = false;
            expander.Unloaded += (_, _) => unloaded = true;
            root.Children.Remove(expander);
            await Until(() => unloaded);
            check(Active(expander) is null && double.IsNaN(viewport.Height) && viewport.Visibility == Visibility.Visible,
                "Unloading stops expansion and normalizes height while preserving the requested expanded state");
            var reloaded = false;
            expander.Loaded += (_, _) => reloaded = true;
            root.Children.Insert(0, expander);
            await Until(() => reloaded);
            root.UpdateLayout();
            check(expander.IsExpanded && double.IsNaN(viewport.Height) && viewport.ActualHeight > 0,
                "Reloading an expanded row restores automatic layout without reviving an old storyboard");

            var savedPosition = settings.Current.Position;
            settings.Save(settings.Current with { Animations = true });
            var sample = Tree(preview).OfType<Border>().Single(border => AutomationProperties.GetAutomationId(border) == "AppearancePreviewIsland");
            if (SettingsMotion.Allow(preview) && !new AccessibilitySettings().HighContrast)
            {
                await Until(() => Active(preview) is not null);
                check(Active(preview)!.Children.OfType<DoubleAnimation>().Any(item => Storyboard.GetTargetProperty(item) == "Opacity")
                    && settings.Current.Position == savedPosition && actualPreviews == 0,
                    "Enabling animation starts one visible simulation demonstration without changing position or invoking the real host preview");
                settings.Save(settings.Current with { Animations = false });
                check(Active(preview) is null && Math.Abs(sample.Opacity - 1) < 0.001,
                    "Disabling animation stops the simulation immediately and restores full content opacity");
                settings.Save(settings.Current with { Animations = true });
                await Until(() => Active(preview) is not null);
                var previewUnloaded = false;
                preview.Unloaded += (_, _) => previewUnloaded = true;
                root.Children.Remove(preview);
                await Until(() => previewUnloaded);
                check(Active(preview) is null && Math.Abs(sample.Opacity - 1) < 0.001 && settings.Current.Position == savedPosition,
                    "Unloading stops the simulation and leaves the persisted position unchanged");
            }
            else
            {
                await Task.Delay(SettingsMotion.Milliseconds + 40);
                check(Active(preview) is null && Math.Abs(sample.Opacity - 1) < 0.001 && actualPreviews == 0,
                    "The simulation honors the actual system motion or high-contrast restriction without showing a host notification");
            }
        }
        finally { preview.Dispose(); window.Close(); }
    }

    private static Storyboard? Active(object component) => component.GetType()
        .GetField("_animation", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(component) as Storyboard;
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The isolated motion fixture did not reach its expected state.");
            await Task.Delay(20);
        }
    }
}
