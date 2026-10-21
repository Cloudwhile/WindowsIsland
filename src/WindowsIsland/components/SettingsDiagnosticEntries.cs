using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

/// <summary>保持展开状态的扁平诊断记录列表，内容来自有界的本机快照。</summary>
internal sealed class SettingsDiagnosticEntries : StackPanel
{
    private sealed class EntryView
    {
        public SettingsExpander Row { get; }
        private readonly StackPanel _details = new() { Spacing = 12, Margin = new Thickness(12, 8, 12, 16) };
        private DiagnosticEntry? _entry;
        private string? _language;

        public EntryView(DiagnosticEntry entry)
        {
            Row = new SettingsExpander(new SymbolIcon(Symbol.Important), "", "DiagnosticEntry." + entry.Id,
                "DiagnosticOutcome." + entry.Id, localizeTitle: false) { ExpandOnHeaderTap = true };
            Row.Rows.Add(_details);
            Update(entry);
        }

        public void Update(DiagnosticEntry entry)
        {
            if (_entry == entry && _language == Localization.Language) return;
            _entry = entry;
            _language = Localization.Language;
            var title = DiagnosticPresentation.Title(entry);
            // 保持行和展开状态，只替换有变化的文案。
            Row.Header.Title = title;
            AutomationProperties.SetName(Row, title);
            Row.Header.Status = DiagnosticPresentation.Summary(entry);
            Row.Header.Tone = DiagnosticPresentation.Outcome(entry) == DiagnosticOutcome.Success ? StatusTone.Success : StatusTone.Critical;
            _details.Children.Clear();
            Detail(Localization.Format("DiagnosticTime", entry.FirstSeen.ToLocalTime().ToString("g", Localization.Culture),
                entry.LastSeen.ToLocalTime().ToString("g", Localization.Culture)), "Time");
            Detail(entry.ClientVersion is null ? Localization.Format("DiagnosticVersion", entry.AppVersion)
                : Localization.Format("DiagnosticVersionsWithClient", entry.AppVersion, DiagnosticPresentation.Area(entry), entry.ClientVersion), "Version");
            if (DiagnosticPresentation.Reason(entry) is { } reason) Detail(Localization.Format("DiagnosticReason", reason), "Reason");
            if (DiagnosticPresentation.Suggestion(entry) is { } suggestion) Detail(Localization.Format("DiagnosticSuggestion", suggestion), "Suggestion");
            Detail(Localization.Format("DiagnosticShortId", DiagnosticPresentation.ShortId(entry)), "Id");
        }

        private void Detail(string text, string field)
        {
            var value = IslandTheme.Text(text, 12, secondary: true);
            value.TextWrapping = TextWrapping.Wrap;
            value.IsTextSelectionEnabled = true;
            AutomationProperties.SetAutomationId(value, "DiagnosticDetail." + _entry!.Id + "." + field);
            _details.Children.Add(value);
        }
    }

    private readonly Dictionary<string, EntryView> _rows = [];

    public void Refresh(DiagnosticEntry[] entries)
    {
        var ids = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            Children.Remove(_rows[id].Row);
            _rows.Remove(id);
        }
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (!_rows.TryGetValue(entry.Id, out var view))
            {
                view = new EntryView(entry);
                _rows.Add(entry.Id, view);
                Children.Add(view.Row);
            }
            else view.Update(entry);
            if (Children.IndexOf(view.Row) != index)
            {
                Children.Remove(view.Row);
                Children.Insert(index, view.Row);
            }
        }
        Visibility = entries.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
