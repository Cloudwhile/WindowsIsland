using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed partial class PluginDesktopBroker
{
    private sealed class Tree(int processId, nint window, DesktopTree value, AutomationElement[] elements)
    {
        public readonly int ProcessId = processId;
        public readonly nint Window = window;
        public readonly DesktopTree Value = value;
        public readonly AutomationElement[] Elements = elements;
        public readonly long Created = Stopwatch.GetTimestamp();
        public System.Drawing.Bitmap? Frame;
        public NativeRect FrameBounds;
        public HashSet<int> Captured = [];
        public void Dispose() => Frame?.Dispose();
    }
    private readonly Dictionary<string, Tree> _trees = [];

    public Task<DesktopTree> ReadTreeAsync(int processId, long window, CancellationToken token = default) => RunAsync(() =>
    {
        _ = GetClient(processId);
        var handle = new nint(window);
        CheckWindow(processId, handle);
        foreach (var key in _trees.Where(pair => Stopwatch.GetElapsedTime(pair.Value.Created) > TimeSpan.FromSeconds(5)
            || pair.Value.Window == handle).Select(pair => pair.Key).ToArray()) { _trees[key].Dispose(); _trees.Remove(key); }
        while (_trees.Count >= 8) { var key = _trees.Keys.First(); _trees[key].Dispose(); _trees.Remove(key); }
        var root = AutomationElement.FromHandle(handle) ?? throw new IOException("Window accessibility is unavailable.");
        var nodes = new List<DesktopNode>();
        var elements = new List<AutomationElement>();
        var queue = new Queue<(AutomationElement Element, int Parent, int Depth)>();
        queue.Enqueue((root, -1, 0));
        var timer = Stopwatch.StartNew();
        var textLength = 0;
        var walker = TreeWalker.ControlViewWalker;
        var cache = new CacheRequest();
        foreach (var property in new[] { AutomationElement.NameProperty, AutomationElement.AutomationIdProperty,
            AutomationElement.ClassNameProperty, AutomationElement.ControlTypeProperty, AutomationElement.ItemStatusProperty,
            AutomationElement.ItemTypeProperty, AutomationElement.BoundingRectangleProperty, AutomationElement.IsOffscreenProperty,
            AutomationElement.ProcessIdProperty, AutomationElement.IsPasswordProperty }) cache.Add(property);
        while (queue.TryDequeue(out var item))
        {
            token.ThrowIfCancellationRequested(); _lifetime.Token.ThrowIfCancellationRequested();
            if (nodes.Count >= 2048 || timer.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidDataException("Desktop tree exceeds the limit.");
            var info = item.Element.GetUpdatedCache(cache).Cached;
            if (info.ProcessId != processId) continue;
            var name = info.IsPassword ? "" : Text(info.Name, 4096);
            if (name.Length == 0 && !info.IsPassword && info.ControlType is var controlType && (controlType == ControlType.Text || controlType == ControlType.Document)
                && item.Element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
                name = Text(((TextPattern)textPattern).DocumentRange.GetText(2048), 2048);
            var automationId = Text(info.AutomationId, 1024); var className = Text(info.ClassName, 256);
            var status = Text(info.ItemStatus, 256); var type = Text(info.ItemType, 256);
            textLength += name.Length + automationId.Length + className.Length + status.Length + type.Length;
            if (textLength > 512 * 1024) throw new InvalidDataException("Desktop text exceeds the limit.");
            var bounds = info.BoundingRectangle;
            var index = nodes.Count;
            nodes.Add(new(item.Parent, name, automationId, className, info.ControlType.Id, status, type,
                string.Join('/', item.Element.GetRuntimeId()), bounds.IsEmpty ? default : new(bounds.X, bounds.Y, bounds.Width, bounds.Height), info.IsOffscreen));
            elements.Add(item.Element);
            if (item.Depth >= 24) continue;
            var children = 0;
            for (var child = walker.GetFirstChild(item.Element); child is not null; child = walker.GetNextSibling(child))
            {
                if (++children > 512 || nodes.Count + queue.Count >= 2048) throw new InvalidDataException("Desktop tree exceeds the limit.");
                queue.Enqueue((child, index, item.Depth + 1));
            }
        }
        CheckWindow(processId, handle);
        var tree = new DesktopTree(Guid.NewGuid().ToString("N"), nodes.ToArray()) { ObservedAt = DateTimeOffset.Now };
        _trees.Add(tree.Token, new(processId, handle, tree, elements.ToArray()));
        return tree;
    }, token);

    private static string Text(string? value, int maximum) => value is null ? "" : value.Length <= maximum ? value : value[..maximum];
    private static void CheckWindow(int processId, nint handle)
    {
        GetWindowThreadProcessId(handle, out var owner);
        if (owner != processId || !IsWindow(handle)) throw new UnauthorizedAccessException();
    }

    private static bool Current(Tree tree, int index)
    {
        try
        {
            CheckWindow(tree.ProcessId, tree.Window);
            var old = tree.Value.Nodes[index];
            var element = tree.Elements[index];
            var cache = new CacheRequest();
            foreach (var property in new[] { AutomationElement.NameProperty, AutomationElement.AutomationIdProperty,
                AutomationElement.ClassNameProperty, AutomationElement.IsOffscreenProperty, AutomationElement.BoundingRectangleProperty,
                AutomationElement.ProcessIdProperty }) cache.Add(property);
            var current = element.GetUpdatedCache(cache).Cached;
            var rect = current.BoundingRectangle;
            return current.ProcessId == tree.ProcessId && current.Name == old.Name && current.AutomationId == old.AutomationId
                && current.ClassName == old.ClassName && current.IsOffscreen == old.Offscreen
                && (rect.IsEmpty ? default : new DesktopRect(rect.X, rect.Y, rect.Width, rect.Height)) == old.Bounds
                && string.Join('/', element.GetRuntimeId()) == old.RuntimeId;
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException) { return false; }
    }
    private void ClearTrees() { foreach (var tree in _trees.Values) tree.Dispose(); _trees.Clear(); }
}
